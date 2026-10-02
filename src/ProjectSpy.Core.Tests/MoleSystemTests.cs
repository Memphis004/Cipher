using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The mole system: silent weekly Heat, mission leaks, counter-intelligence
/// investigations, and the cost of a wrong accusation.
/// </summary>
/// <remarks>
/// The tests are organised around the falsifiability argument in
/// <c>docs/MOLE_DESIGN.md</c>: a mole is only a fair mystery if its contribution is
/// deterministic, its timing is correlatable, and its investigation yields graded
/// evidence rather than a verdict.
/// </remarks>
public class MoleSystemTests
{
    private const ulong Seed = 90210UL;

    // ---- silent heat ---------------------------------------------------------

    [Fact]
    public void AMoleIsFoundWhetherOrNotTheTraitIsRevealed()
    {
        // If the weekly pass missed hidden moles, uncovering one would quietly switch
        // off the heat it had been generating — a reward for playing well rather than a
        // consequence of having a mole.
        GameSession session = World.Session(Seed);
        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        Assert.False(mole.HasRevealedTrait(World.Traits.Mole));
        Assert.Contains(mole, MoleSystem.FindMoles(session.World));
    }

    [Fact]
    public void WeeklyHeatIsAppliedExactlyOncePerMole()
    {
        GameSession session = World.Session(Seed);
        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        int before = session.World.Resources.Heat;
        int added = MoleSystem.ApplyWeeklyMoleHeat(session.World, null);

        Assert.Equal(MoleSystem.WeeklyHeat, added);
        Assert.Equal(before + MoleSystem.WeeklyHeat, session.World.Resources.Heat);
    }

    [Fact]
    public void NoMolesMeansNoHiddenHeat()
    {
        GameSession session = World.Session(Seed);
        World.Hire(session);
        World.Hire(session);

        Assert.Empty(MoleSystem.FindMoles(session.World));
        Assert.Equal(0, MoleSystem.ApplyWeeklyMoleHeat(session.World, null));
    }

    [Fact]
    public void TwoMolesProduceTwiceTheHeat()
    {
        GameSession session = World.Session(Seed);
        World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });
        World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        Assert.Equal(2 * MoleSystem.WeeklyHeat, MoleSystem.ApplyWeeklyMoleHeat(session.World, null));
    }

    [Fact]
    public void AHeatNeutralisingTraitReducesTheSpike()
    {
        // A patriot talking the mole's game down is a counter-play, not a free win: the
        // mole is still there, just quieter.
        int patriot = 0;
        int reduction = 0;
        foreach (Trait trait in TraitLookup.All())
        {
            if (trait.EffectType == "HeatGainReduction")
            {
                patriot = trait.Id;
                reduction = trait.EffectValue;
                break;
            }
        }

        Assert.True(patriot > 0, "the trait table must ship a heat-reduction trait");
        Assert.True(reduction >= MoleSystem.WeeklyHeat,
            $"this test is only meaningful while the reduction ({reduction}) can outweigh "
            + $"the weekly heat ({MoleSystem.WeeklyHeat})");

        GameSession session = World.Session(Seed);
        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole, patriot });

        int added = MoleSystem.ApplyWeeklyMoleHeat(session.World, null);

        Assert.True(
            added < MoleSystem.WeeklyHeat && added > 0,
            $"expected a reduced but non-zero spike, got {added} against {MoleSystem.WeeklyHeat}");
    }

    [Fact]
    public void NoTraitCombinationCanSilenceTheMoleEntirely()
    {
        // Every heat-reduction trait at once, and the spike must survive. If a patriot
        // could zero it there would be no tell left and the mole would cost nothing.
        GameSession session = World.Session(Seed);

        var reducers = TraitLookup.All()
            .Where(t => t.EffectType == "HeatGainReduction")
            .Select(t => t.Id)
            .ToArray();

        Assert.NotEmpty(reducers);

        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole }.Concat(reducers).ToArray());

        Assert.True(MoleSystem.ApplyWeeklyMoleHeat(session.World, null) > 0,
            "no amount of counter-play may remove the mole's heat signature entirely");
    }

    [Fact]
    public void HeatIsZeroWithoutAMoleEveryWeekOfALongRun()
    {
        // The falsifiability baseline: with no mole the weekly heat pass is a no-op, so
        // any spike the player sees is attributable to something.
        GameSession session = World.Session(Seed);
        for (int i = 0; i < 4; i++)
            World.Hire(session);

        for (int week = 0; week < 20; week++)
        {
            Assert.Equal(0, MoleSystem.ApplyWeeklyMoleHeat(session.World, null));
        }

        Assert.Equal(0, session.World.Resources.Heat);
    }

    // ---- mission leaks -------------------------------------------------------

    [Fact]
    public void NoMoleMeansNoLeakEver()
    {
        GameSession session = World.Session(Seed);
        for (int i = 0; i < 3; i++)
            World.Hire(session);

        for (int mission = 0; mission < 100; mission++)
            Assert.Null(MoleSystem.RollMissionLeak(session.World, mission, null));

        Assert.Empty(session.World.CounterIntel.LeakLog);
    }

    [Fact]
    public void AMoleLeaksWithinAFiniteNumberOfMissions()
    {
        // "Sometimes" is fine; "almost never" would make the mole cosmetic.
        GameSession session = World.Session(Seed);
        World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        bool leaked = false;
        for (int mission = 0; mission < 200 && !leaked; mission++)
            leaked = MoleSystem.RollMissionLeak(session.World, mission, null) is not null;

        Assert.True(leaked, "the mole never leaked in 200 missions");
    }

    [Fact]
    public void EveryLeakRaisesDifficultyAndHeat()
    {
        GameSession session = World.Session(Seed);
        World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        MoleLeak? leak = null;
        for (int mission = 0; mission < 200 && leak is null; mission++)
            leak = MoleSystem.RollMissionLeak(session.World, mission, null);

        Assert.NotNull(leak);
        Assert.True(leak!.DifficultyBonus > 0, "a leak must make the job harder");
        Assert.True(leak.HeatAdded > 0, "a leak must leave a trace");
    }

    [Fact]
    public void APreExposureLeakIsNotPublic()
    {
        // Before exposure the player sees "something went wrong", not "someone in my
        // house did it".
        GameSession session = World.Session(Seed);
        World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        MoleLeak? leak = null;
        for (int mission = 0; mission < 200 && leak is null; mission++)
            leak = MoleSystem.RollMissionLeak(session.World, mission, null);

        Assert.NotNull(leak);
        Assert.False(leak!.IsPublic);
    }

    [Fact]
    public void AccumulatedLeakDifficultyOnlyEverGrows()
    {
        GameSession session = World.Session(Seed);
        World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        int previous = 0;
        for (int mission = 0; mission < 60; mission++)
        {
            MoleSystem.RollMissionLeak(session.World, mission, null);

            int total = MoleSystem.AccumulatedLeakDifficulty(session.World);
            Assert.True(total >= previous, "the leak difficulty is the stage-4 seam and must be monotonic");
            previous = total;
        }
    }

    [Fact]
    public void ALookupWithNoLeaksCostsThePlayerNothing()
    {
        GameSession session = World.Session(Seed);
        Assert.Equal(0, MoleSystem.AccumulatedLeakDifficulty(session.World));
    }

    // ---- investigations ------------------------------------------------------

    [Fact]
    public void WithoutACounterIntelDeskNoInvestigationCanOpen()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);

        Assert.Equal(0, MoleSystem.CounterIntelRoomLevel(session.World));
        Assert.Null(MoleSystem.StartInvestigation(session.World, agent.Id, null));
    }

    [Fact]
    public void ACounterIntelDeskOpensInvestigations()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent agent = World.Hire(session);

        Assert.Equal(1, MoleSystem.CounterIntelRoomLevel(session.World));
        Assert.NotNull(MoleSystem.StartInvestigation(session.World, agent.Id, null));
    }

    [Fact]
    public void InvestigatingTheSameAgentTwiceIsRejected()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent agent = World.Hire(session);

        Assert.NotNull(MoleSystem.StartInvestigation(session.World, agent.Id, null));
        Assert.Null(MoleSystem.StartInvestigation(session.World, agent.Id, null));
    }

    [Fact]
    public void InvestigatingSomebodyWhoDoesNotExistIsRejected()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Assert.Null(MoleSystem.StartInvestigation(session.World, new AgentId(9999), null));
    }

    [Fact]
    public void CapacityIsTheProductOfDeskLevelAndTheTable()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 3;

        Assert.Equal(3, MoleSystem.Capacity(session.World));
    }

    [Fact]
    public void TheDeskRefusesMoreCasesThanItHasCapacityFor()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent a = World.Hire(session);
        Agent b = World.Hire(session);

        Assert.NotNull(MoleSystem.StartInvestigation(session.World, a.Id, null));
        Assert.Null(MoleSystem.StartInvestigation(session.World, b.Id, null));
    }

    [Fact]
    public void EvidenceStaysInRangeOverALongInvestigation()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent agent = World.Hire(session);
        MoleSystem.StartInvestigation(session.World, agent.Id, null);

        InvestigationState? investigation = session.World.CounterIntel.FindFor(agent.Id);
        Assert.NotNull(investigation);

        for (int tick = 0; tick < 3000; tick++)
        {
            MoleSystem.TickInvestigations(session.World, null);

            Assert.InRange(investigation!.Evidence, 0, 100);
        }
    }

    [Fact]
    public void EvidenceIsGradedRatherThanVerdictInOneStep()
    {
        // The player must be able to watch a case build, not be handed an answer.
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent agent = World.Hire(session);
        MoleSystem.StartInvestigation(session.World, agent.Id, null);

        InvestigationState? investigation = session.World.CounterIntel.FindFor(agent.Id);
        Assert.NotNull(investigation);

        // One step's worth of ticks must not be able to conclude the case.
        int ticksPerStep = SimulationRules.CounterIntel("investigation_ticks_per_step", 24);
        for (int tick = 0; tick < ticksPerStep; tick++)
            MoleSystem.TickInvestigations(session.World, null);

        Assert.True(investigation!.IsRunning, "a single step must not conclude an investigation");
        Assert.True(investigation.Evidence < MoleSystem.ExposeThreshold);
    }

    [Fact]
    public void EvidenceClimbsAndDipsOverManySteps()
    {
        // False leads must genuinely lower evidence, so a weak case deteriorates and the
        // player is made cautious about accusing.
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent agent = World.Hire(session);
        MoleSystem.StartInvestigation(session.World, agent.Id, null);

        InvestigationState? investigation = session.World.CounterIntel.FindFor(agent.Id);
        Assert.NotNull(investigation);

        bool sawIncrease = false;
        bool sawDecrease = false;
        int previous = investigation!.Evidence;

        for (int tick = 0; tick < 4000; tick++)
        {
            MoleSystem.TickInvestigations(session.World, null);

            if (investigation.Evidence > previous) sawIncrease = true;
            if (investigation.Evidence < previous) sawDecrease = true;

            previous = investigation.Evidence;
        }

        Assert.True(sawIncrease, "evidence never rose over 4000 ticks");
        Assert.True(sawDecrease, "a false lead never lowered the evidence");
    }

    [Fact]
    public void AConclusiveCaseExposesTheMole()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });
        MoleSystem.StartInvestigation(session.World, mole.Id, null);

        InvestigationState? investigation = session.World.CounterIntel.FindFor(mole.Id);
        Assert.NotNull(investigation);

        // Start from a conclusive score rather than rolling for one. Accumulating the
        // evidence organically is probabilistic, so a test that depends on it is really
        // a test of the seed; this one tests the rule.
        investigation!.Evidence = MoleSystem.ExposeThreshold;
        RunSteps(session, 1);

        Assert.False(investigation.IsRunning, "a case must conclude when it clears the threshold");
        Assert.Equal(InvestigationStatus.Exposed, investigation.Status);
        Assert.True(mole.HasRevealedTrait(World.Traits.Mole),
            "concluding mid-investigation must expose the agent, not just announce a verdict");
        Assert.Equal(1, session.World.CounterIntel.ExposedCount);
    }

    /// <summary>
    /// Ticks until the desk has completed roughly <paramref name="steps"/> of work.
    /// </summary>
    private static void RunSteps(GameSession session, int steps)
    {
        int ticksPerStep = SimulationRules.CounterIntel("investigation_ticks_per_step", 24);

        for (int i = 0; i < steps * ticksPerStep + ticksPerStep; i++)
            MoleSystem.TickInvestigations(session.World, null);
    }

    [Fact]
    public void AnInconclusiveCaseClearsTheAgent()
    {
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent innocent = World.Hire(session);
        MoleSystem.StartInvestigation(session.World, innocent.Id, null);

        InvestigationState? investigation = session.World.CounterIntel.FindFor(innocent.Id);
        Assert.NotNull(investigation);

        // No evidence at all, so the step budget is what ends the case.
        RunSteps(session, MoleSystem.MaxSteps * 3);
        MoleSystem.ExpireStaleInvestigations(session.World, MoleSystem.MaxSteps, null);

        Assert.False(investigation!.IsRunning);
        Assert.Equal(InvestigationStatus.Cleared, investigation.Status);
        Assert.False(innocent.HasRevealedTrait(World.Traits.Mole));
    }

    [Fact]
    public void AConclusiveCaseAgainstAnInnocentAgentClearsThemWithoutExposing()
    {
        // Reaching the threshold is not the same as being the mole: the desk grades a
        // case, it does not declare a verdict on the subject's character.
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent innocent = World.Hire(session);
        MoleSystem.StartInvestigation(session.World, innocent.Id, null);

        InvestigationState? investigation = session.World.CounterIntel.FindFor(innocent.Id);
        Assert.NotNull(investigation);

        investigation!.Evidence = MoleSystem.ExposeThreshold;
        RunSteps(session, 1);

        Assert.False(investigation.IsRunning);
        Assert.Equal(InvestigationStatus.Cleared, investigation.Status);
        Assert.Equal(0, session.World.CounterIntel.ExposedCount);
    }

    // ---- accusation ----------------------------------------------------------

    [Fact]
    public void AccusingTheMoleIsCorrectAndExposesThem()
    {
        GameSession session = World.Session(Seed);
        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        var sink = new RecordingSink();
        Assert.True(MoleSystem.Accuse(session.World, mole.Id, sink));

        Assert.True(mole.HasRevealedTrait(World.Traits.Mole));
        Assert.Equal(0, session.World.CounterIntel.WrongAccusations);
        Assert.Equal(1, session.World.CounterIntel.ExposedCount);
        Assert.True(sink.Saw(GameEventKind.AgentExposedAsMole));
    }

    [Fact]
    public void AccusingTheWrongAgentCostsOrganisationWideLoyalty()
    {
        // The damage is the atmosphere of a place where accusing people gets people
        // fired, not just the accused agent's feelings.
        GameSession session = World.Session(Seed);
        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });
        Agent scapegoat = World.Hire(session);
        Agent bystander = World.Hire(session);

        Assert.True(MoleSystem.Accuse(session.World, scapegoat.Id, null));

        Assert.True(scapegoat.Loyalty < Agent.StartingLoyalty, "the accused pays too");
        Assert.True(
            bystander.Loyalty < Agent.StartingLoyalty,
            $"the bystander paid nothing: {bystander.Loyalty}");
        Assert.True(mole.Loyalty < Agent.StartingLoyalty, "the whole organisation is affected");
    }

    [Fact]
    public void WrongAccusationsAreCounted()
    {
        GameSession session = World.Session(Seed);
        Agent innocent = World.Hire(session);

        MoleSystem.Accuse(session.World, innocent.Id, null);

        Assert.Equal(1, session.World.CounterIntel.WrongAccusations);
        Assert.Equal(0, session.World.CounterIntel.ExposedCount);
    }

    [Fact]
    public void AccusingNobodyIsRejected()
    {
        GameSession session = World.Session(Seed);
        Assert.False(MoleSystem.Accuse(session.World, new AgentId(9999), null));
    }

    [Fact]
    public void AccusingDoesNotEndTheMolesHeatWhileUnproven()
    {
        // Heat keeps accruing until the player actually does the work.
        GameSession session = World.Session(Seed);
        Room desk = World.Place(session, World.Rooms.CounterIntel, 0, 0, 3);
        desk.Level = 1;

        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });
        MoleSystem.StartInvestigation(session.World, mole.Id, null);
        MoleSystem.TickInvestigations(session.World, null);

        Assert.True(MoleSystem.ApplyWeeklyMoleHeat(session.World, null) > 0);
    }

    [Fact]
    public void ExposingAMoleDoesNotStopTheHeatPassFromFindingThem()
    {
        // Exposed is still a mole; the weekly pass must keep counting them so the
        // player still suffers the consequence of having had one.
        GameSession session = World.Session(Seed);
        Agent mole = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });

        MoleSystem.Accuse(session.World, mole.Id, null);
        Assert.Contains(mole, MoleSystem.FindMoles(session.World));
    }

    // ---- table integrity -----------------------------------------------------

    [Fact]
    public void EveryCounterIntelKeyTheSystemReadsExistsInTheTable()
    {
        // A typo'd key falls back to the supplied default and the penalty quietly
        // disappears, which is the failure mode this guards against.
        string[] keys =
        {
            "mole_weekly_heat",
            "mole_leak_chance_percent",
            "mole_leak_difficulty_bonus",
            "investigation_ticks_per_step",
            "investigation_evidence_chance",
            "investigation_false_lead_chance",
            "investigation_evidence_gain",
            "investigation_false_lead_penalty",
            "investigation_max_steps",
            "expose_threshold",
            "mole_heat_floor_percent",
            "counter_intel_capacity_per_level",
            "wrong_accusation_loyalty_drain",
        };

        var present = SimulationRules.AllCounterIntel()
            .Select(c => c.RuleKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string key in keys)
            Assert.Contains(key, present);
    }

    [Fact]
    public void LeakAndEvidenceChancesAreAValidDistribution()
    {
        int evidenceChance = SimulationRules.CounterIntel("investigation_evidence_chance", 35);
        int falseLeadChance = SimulationRules.CounterIntel("investigation_false_lead_chance", 20);

        Assert.InRange(evidenceChance, 0, 100);
        Assert.InRange(falseLeadChance, 0, 100);
        Assert.True(evidenceChance + falseLeadChance <= 100, "the two branches must not overlap or overflow");
    }

    [Fact]
    public void TheExposeThresholdIsReachableWithinTheStepBudget()
    {
        // The regression this guards: evidence used to move by one per step against a
        // threshold of 80, so no case could ever conclude and the mole was uncatchable
        // by investigation. Both numbers are now table values; the relationship between
        // them is what has to hold.
        int gain = SimulationRules.CounterIntel("investigation_evidence_gain", 10);
        int maxSteps = MoleSystem.MaxSteps;

        Assert.InRange(MoleSystem.ExposeThreshold, 1, 100);
        Assert.True(
            maxSteps * gain > MoleSystem.ExposeThreshold,
            $"{maxSteps} steps at {gain} evidence reaches {maxSteps * gain}, "
            + $"below the threshold of {MoleSystem.ExposeThreshold}");
    }

    [Fact]
    public void EvidenceIsNotGuaranteedWithinTheStepBudget()
    {
        // The mirror image: if the average case cleared the threshold the desk would be
        // a mole detector and the mystery would be pointless. Accusing on a hunch has to
        // remain usually wrong.
        int gain = SimulationRules.CounterIntel("investigation_evidence_gain", 10);
        int penalty = SimulationRules.CounterIntel("investigation_false_lead_penalty", 8);
        int evidenceChance = SimulationRules.CounterIntel("investigation_evidence_chance", 35);
        int falseLeadChance = SimulationRules.CounterIntel("investigation_false_lead_chance", 20);

        int expected = MoleSystem.MaxSteps * (evidenceChance * gain - falseLeadChance * penalty) / 100;

        Assert.True(expected < MoleSystem.ExposeThreshold,
            $"the expected case reaches {expected}, at or above the threshold of "
            + $"{MoleSystem.ExposeThreshold} — the desk would expose anyone on average");
    }
}
