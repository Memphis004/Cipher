using ProjectSpy.Core.Tactical;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The tactical step order contract.
/// </summary>
/// <remarks>
/// <para>
/// Rule 6 names the step order as part of the save and replay contract:
/// <c>ApplyQueuedCommands → MovementResolution → ActionProgress → NoisePropagation →
/// Perception → NpcPlanning → AlarmUpdate → DamageAndStatus → ObjectiveCheck →
/// EventEmit</c>. Reordering changes every outcome for an existing save and silently
/// invalidates every recorded replay, so the order is declared once and asserted here
/// rather than trusted to whoever writes the pipeline.
/// </para>
/// <para>
/// The pairwise tests below are the ones that matter. A test that only asserts "the
/// order is this list" passes just as happily against an order that is correct in
/// sequence and wrong in consequence — and the sequence is the easy half. Each pairwise
/// test states a dependency and shows what reversing it costs, so a future reorder fails
/// as a named constraint rather than as an unexplained change in outcomes.
/// </para>
/// </remarks>
public class TacticalStepOrderTests
{
    [Fact]
    public void TheCanonicalOrderIsTheOrderTheBriefSpecifies()
    {
        Assert.Equal(
            new[]
            {
                TacticalStepPhase.ApplyQueuedCommands,
                TacticalStepPhase.MovementResolution,
                TacticalStepPhase.ActionProgress,
                TacticalStepPhase.NoisePropagation,
                TacticalStepPhase.Perception,
                TacticalStepPhase.NpcPlanning,
                TacticalStepPhase.AlarmUpdate,
                TacticalStepPhase.DamageAndStatus,
                TacticalStepPhase.ObjectiveCheck,
                TacticalStepPhase.EventEmit,
            },
            TacticalStepPipeline.CanonicalOrder);
    }

    [Fact]
    public void EveryPhaseValueHasASlotInTheCanonicalOrder()
    {
        // A phase added to the enum without a slot would silently never run.
        var ordered = TacticalStepPipeline.CanonicalOrder.ToHashSet();

        foreach (TacticalStepPhase phase in Enum.GetValues<TacticalStepPhase>())
            Assert.Contains(phase, ordered);
    }

    [Fact]
    public void TheCanonicalOrderContainsNoDuplicates()
    {
        Assert.Equal(
            TacticalStepPipeline.CanonicalOrder.Count,
            TacticalStepPipeline.CanonicalOrder.Distinct().Count());
    }

    [Fact]
    public void TheDefaultPipelineRegistersEveryPhase()
    {
        TacticalStepPipeline pipeline = TacticalPhases.CreateDefault();

        var registered = pipeline.Registered.Select(p => p.Phase).ToHashSet();

        foreach (TacticalStepPhase phase in TacticalStepPipeline.CanonicalOrder)
            Assert.Contains(phase, registered);
    }

    [Fact]
    public void ASingleStepRunsEveryPhaseExactlyOnceInCanonicalOrder()
    {
        TacticalMissionRunner runner = TacticalHarness.Runner();
        IReadOnlyList<TacticalPhaseReport> reports = runner.Step();

        Assert.Equal(TacticalStepPipeline.CanonicalOrder, reports.Select(r => r.Phase));
    }

    [Fact]
    public void ASingleStepAdvancesTheMissionByExactlyOne()
    {
        TacticalMissionRunner runner = TacticalHarness.Runner();
        Assert.Equal(0, runner.State.Step);

        runner.Step();

        Assert.Equal(1, runner.State.Step);
        Assert.Equal(1, runner.StepsRun);
    }

    // ---- the pairwise constraints -------------------------------------------

    [Fact]
    public void MovementRunsBeforeActionsSoAPositionChangeIsCompleteBeforeANewAction()
    {
        // Reversed, a takedown begun this step would be resolved against where the
        // attacker was before this step's movement — and would connect to a target they
        // had just walked away from.
        AssertRunsBefore(
            TacticalStepPhase.MovementResolution,
            TacticalStepPhase.ActionProgress);
    }

    [Fact]
    public void MovementAndActionsBothRunBeforeNoiseSoEverythingMadeThisStepIsHeardThisStep()
    {
        // Reversed, the footstep a guard hears would be one step late: a burst of
        // running would be heard only after it had finished, which is the difference
        // between a chase and a coincidence.
        AssertRunsBefore(TacticalStepPhase.MovementResolution, TacticalStepPhase.NoisePropagation);
        AssertRunsBefore(TacticalStepPhase.ActionProgress, TacticalStepPhase.NoisePropagation);
    }

    [Fact]
    public void NoiseRunsBeforePerceptionSoAGuardTurnsTowardWhatTheyHeardThisStep()
    {
        // Reversed, a guard would hear a noise and then perceive along the facing they
        // had *before* hearing it — so backing away from a noise would leave you in the
        // one cone you were not worried about.
        AssertRunsBefore(TacticalStepPhase.NoisePropagation, TacticalStepPhase.Perception);
    }

    [Fact]
    public void PerceptionRunsBeforePlanningSoNPCsReactToThisStepNotTheLast()
    {
        // Reversed, every guard would be a full step behind: they would pursue where the
        // team was, not where they are. That reads as sluggish rather than as deliberate,
        // and it makes a feint work forever.
        AssertRunsBefore(TacticalStepPhase.Perception, TacticalStepPhase.NpcPlanning);
    }

    [Fact]
    public void PlanningRunsBeforeAlarmSoTheAlarmCountsWhatTheNPCsDidThisStep()
    {
        // Reversed, a guard that reached a conclusion this step would not push the alarm
        // until the next one, and every escalation would be a step behind the thing that
        // caused it.
        AssertRunsBefore(TacticalStepPhase.NpcPlanning, TacticalStepPhase.AlarmUpdate);
    }

    [Fact]
    public void AlarmRunsBeforeDamageSoTheSiteReactsToTheNoiseBeforeTheBodyIsFound()
    {
        // Reversed, the site would be calm about a killing it heard nothing of: the body
        // would appear and the alarm would rise only on the step after the noise that
        // came with it.
        AssertRunsBefore(TacticalStepPhase.AlarmUpdate, TacticalStepPhase.DamageAndStatus);
    }

    [Fact]
    public void DamageRunsBeforeObjectiveSoADyingSquadIsRecognisedBeforeTheMissionIsCalled()
    {
        // Reversed, a mission could be declared over — everyone at the extraction point,
        // objective complete — with somebody still bleeding out on the way, and the death
        // would land after the debrief that said everybody got out.
        AssertRunsBefore(TacticalStepPhase.DamageAndStatus, TacticalStepPhase.ObjectiveCheck);
    }

    [Fact]
    public void ObjectiveRunsBeforeEventEmitSoEventsDescribeAFinishedStep()
    {
        // Reversed, subscribers would see the step's events before the step had finished
        // resolving — the classic half-applied world that the strategic pipeline's
        // end-of-tick flush exists to prevent, and which UI re-entrancy turns into a
        // spectacular, unreproducible bug.
        AssertRunsBefore(TacticalStepPhase.ObjectiveCheck, TacticalStepPhase.EventEmit);
    }

    [Fact]
    public void QueuedCommandsRunFirstSoAnOrderIsHonouredInTheStepItWasIssued()
    {
        // Reversed, an order would take effect a step after the player issued it, which
        // is a 100 ms lag on every action and — worse — means the command log says a
        // step number that the state does not agree with.
        AssertRunsBefore(TacticalStepPhase.ApplyQueuedCommands, TacticalStepPhase.MovementResolution);
    }

    /// <summary>Asserts <paramref name="first"/> runs before <paramref name="second"/>.</summary>
    private static void AssertRunsBefore(TacticalStepPhase first, TacticalStepPhase second)
    {
        var order = TacticalStepPipeline.CanonicalOrder;

        // IndexOf through LINQ rather than the interface: CanonicalOrder is an
        // IReadOnlyList, and the point of this helper is to read the declaration, not to
        // assume it is a concrete list.
        int a = order.ToList().IndexOf(first);
        int b = order.ToList().IndexOf(second);

        Assert.True(a >= 0, $"{first} has no slot in the canonical order.");
        Assert.True(b >= 0, $"{second} has no slot in the canonical order.");
        Assert.True(a < b, $"{first} must run before {second} but the canonical order says otherwise.");
    }

    // ---- the two orders never interleave -------------------------------------

    [Fact]
    public void TheTacticalOrderIsSeparateFromTheStrategicOneAndDoesNotInterleave()
    {
        // Rule 6: two orders, never interleaved. The enum names are deliberately
        // disjoint — no TickPhase has a StepPhase counterpart — so a phase cannot be
        // shared between the two pipelines by accident.
        var strategic = Enum.GetValues<TickPhase>().Select(p => p.ToString()).ToHashSet();
        var tactical = Enum.GetValues<TacticalStepPhase>().Select(p => p.ToString()).ToHashSet();

        Assert.Empty(strategic.Intersect(tactical));
    }

    [Fact]
    public void TheBaseClockDoesNotMoveWhileTacticalStepsRun()
    {
        GameSession session = TacticalHarness.PlayingSession();
        Tick before = session.CurrentTick;

        session.AdvanceTacticalSteps(50);

        Assert.Equal(before, session.CurrentTick);
        Assert.Equal(SessionMode.Tactical, session.Mode);
    }

    [Fact]
    public void TacticalStepsAdvanceTheTacticalClockAndNotTheBaseOne()
    {
        GameSession session = TacticalHarness.PlayingSession();

        session.AdvanceTacticalSteps(25);

        Assert.Equal(25, session.TacticalClock.Current.Value);
        Assert.Equal(25, session.World.ActiveMission!.Step);
    }
}
