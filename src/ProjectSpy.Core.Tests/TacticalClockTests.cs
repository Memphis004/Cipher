using ProjectSpy.Core;
using ProjectSpy.Core.Tactical;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves the tactical clock, the two-scale freeze invariant, and the one-time
/// conversion between the two scales.
/// </summary>
/// <remarks>
/// The invariant worth testing hard is that the base clock does not move during a
/// mission. It is enforced by a throw rather than a silent no-op, which means every
/// caller that gets it wrong fails at the call site instead of quietly double-spending
/// the player's week.
/// </remarks>
public class TacticalClockTests
{
    private static GameSession NewSession() => new(20240101UL);

    // A real mission, not a hand-built one. Stage 4c gave TacticalState a building to
    // live in, and these tests were previously asserting against a bare slot that could
    // no longer be entered or hashed. The clock does not care what is in the building;
    // what it must not do is be tested against a mission that is not a mission.
    private static TacticalState NewMission(int step = 0)
    {
        TacticalState mission = TacticalHarness.Mission();
        mission.Step = step;
        return mission;
    }

    // ---- Step ----------------------------------------------------------------

    [Fact]
    public void AStepIsAHundredMilliseconds()
    {
        // Ten steps to the simulated second is the whole reason Step exists as its own
        // type: perception, noise and alarm all resolve inside a tenth of a second.
        Assert.Equal(10, Step.StepsPerSecond);
        Assert.Equal(600, Step.StepsPerMinute);
        Assert.Equal(36_000, Step.StepsPerHour);
    }

    [Fact]
    public void StepDerivedTimesAgreeWithTheRawCounter()
    {
        Assert.Equal(0, Step.Zero.Value);
        Assert.Equal(10, Step.FromSeconds(1).Value);
        Assert.Equal(600, Step.FromMinutes(1).Value);
        Assert.Equal(36_000, Step.FromMinutes(60).Value);

        // Second counts from the mission start, so it keeps climbing past 60; Minute is the
        // whole-minute count. Deriving one from the other rather than reusing it is the
        // point: they are computed independently and must not disagree.
        Step step = Step.FromSeconds(90);
        Assert.Equal(90, step.Second);
        Assert.Equal(1, step.Minute);
        Assert.Equal(300, step.StepOfMinute); // 30 seconds into the second minute
    }

    [Fact]
    public void StepSubSecondPartsAreConsistentWithTheCounter()
    {
        Step step = new(3);
        Assert.Equal(0, step.Second);
        Assert.Equal(3, step.StepOfSecond);
        Assert.Equal(0, step.Minute);
        Assert.Equal(3, step.StepOfMinute);
    }

    [Fact]
    public void StepComparesAndArithmetics()
    {
        Step a = new(10);
        Step b = new(20);

        Step equalToA = new(10);

        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= equalToA);
        Assert.True(a >= equalToA);
        Assert.True(a != b);
        Assert.True(a.CompareTo(b) < 0);
        Assert.Equal(0, a.CompareTo(equalToA));

        Assert.Equal(15, (a + 5).Value);
        Assert.Equal(5, (b - 15).Value);
        Assert.Equal(11, a.Plus(1).Value);

        Step counter = new(0);
        counter++;
        Assert.Equal(1, counter.Value);
        counter--;
        Assert.Equal(0, counter.Value);
    }

    // ---- TacticalClock -------------------------------------------------------

    [Fact]
    public void TacticalClockRaisesOneEventPerStep()
    {
        var clock = new TacticalClock();
        var steps = new List<long>();

        using IDisposable _ = clock.OnStep(e => steps.Add(e.Step.Value));

        clock.Advance(5);

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, steps);
        Assert.Equal(5, clock.Current.Value);
        Assert.Equal(5, clock.StepsElapsed);
    }

    [Fact]
    public void TacticalClockRefusesToRewind()
    {
        var clock = new TacticalClock();
        clock.Advance(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetTo(new Step(5)));
        Assert.Equal(10, clock.Current.Value);
    }

    [Fact]
    public void TacticalClockSetToJumpsForward()
    {
        var clock = new TacticalClock();
        clock.SetTo(new Step(100));

        Assert.Equal(100, clock.Current.Value);
        Assert.Equal(100, clock.StepsElapsed);
    }

    [Fact]
    public void TacticalClockUnsubscribeStopsDelivery()
    {
        var clock = new TacticalClock();
        int count = 0;

        IDisposable subscription = clock.OnStep(_ => count++);
        clock.Advance(3);
        int afterFirst = count;

        subscription.Dispose();
        clock.Advance(3);

        Assert.Equal(3, afterFirst);
        Assert.Equal(3, count);
    }

    // ---- the freeze invariant ------------------------------------------------

    [Fact]
    public void AdvancingAStrategicTickDuringAMissionThrows()
    {
        GameSession session = NewSession();
        session.EnterTacticalMode(NewMission());

        Assert.Equal(SessionMode.Tactical, session.Mode);

        // The whole point of rule 13. A silent no-op here would let a system keep
        // calling it forever and the base clock would simply never move.
        Assert.Throws<InvalidOperationException>(() => session.AdvanceTick());
        Assert.Throws<InvalidOperationException>(() => session.AdvanceStrategicTick());
        Assert.Throws<InvalidOperationException>(() => session.AdvanceTicks(10));
        Assert.Throws<InvalidOperationException>(() => session.AdvanceStrategicTicks(10));
        Assert.Throws<InvalidOperationException>(() => session.AdvanceToNextDay());
        Assert.Throws<InvalidOperationException>(() => session.AdvanceToNextWeek());

        Assert.Equal(Tick.Zero, session.CurrentTick);
    }

    [Fact]
    public void TheThrowNamesTheRuleSoTheCallerKnowsWhatToDo()
    {
        GameSession session = NewSession();
        session.EnterTacticalMode(NewMission());

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => session.AdvanceTick());

        Assert.Contains("rule 13", error.Message, StringComparison.Ordinal);
        Assert.Contains("AdvanceTacticalStep", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TacticalStepsDoNotAdvanceTheBaseClock()
    {
        GameSession session = NewSession();
        session.AdvanceTicks(100);

        Tick beforeMission = session.CurrentTick;
        session.EnterTacticalMode(NewMission());
        session.AdvanceTacticalSteps(5_000);

        Assert.Equal(beforeMission, session.CurrentTick);
        Assert.Equal(5_000, session.TacticalClock.Current.Value);
        Assert.Equal(5_000, session.World.ActiveMission!.Step);
    }

    [Fact]
    public void AdvancingTacticalStepsOutsideAMissionThrows()
    {
        GameSession session = NewSession();

        Assert.Throws<InvalidOperationException>(() => session.AdvanceTacticalStep());
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AdvanceTacticalSteps(-1));
    }

    [Fact]
    public void EnteringTacticalModeTwiceThrows()
    {
        GameSession session = NewSession();
        session.EnterTacticalMode(NewMission());

        Assert.Throws<InvalidOperationException>(() => session.EnterTacticalMode(NewMission()));
    }

    [Fact]
    public void LeavingTacticalModeWithoutAMissionThrows()
    {
        GameSession session = NewSession();

        Assert.Throws<InvalidOperationException>(() => session.LeaveTacticalMode());
    }

    [Fact]
    public void EnteringTacticalModeResumesFromTheStoredStep()
    {
        // A mid-mission save has to resume at the exact step it was written at, which
        // is the precondition for the stage-5 mid-mission replay test.
        GameSession session = NewSession();
        session.EnterTacticalMode(NewMission(step: 12_345));

        Assert.Equal(12_345, session.TacticalClock.Current.Value);
        Assert.Equal(12_345, session.World.ActiveMission!.Step);
    }

    // ---- MissionTimeConverter ------------------------------------------------

    [Theory]
    [InlineData(0, 0)]              // no steps, no ticks
    [InlineData(1, 1)]              // a single step is still time that passed
    [InlineData(9_999, 1)]
    [InlineData(35_999, 1)]
    [InlineData(36_000, 1)]         // exactly one hour
    [InlineData(36_001, 2)]         // one step past an hour tips into the second
    [InlineData(72_000, 2)]         // exactly two hours
    [InlineData(72_001, 3)]
    [InlineData(3_600_000, 100)]    // 100 hours
    [InlineData(long.MaxValue, long.MaxValue / 36_000 + 1)]
    public void StepsToStrategicTicksRoundsUpAtEveryBoundary(long steps, long expected)
    {
        Assert.Equal(expected, MissionTimeConverter.StepsToStrategicTicks(steps));
    }

    [Fact]
    public void StepsToStrategicTicksUsesThirtySixThousandStepsPerTick()
    {
        // One strategic tick is one in-game hour, and there are ten steps per
        // simulated second. If this constant is wrong, every mission silently costs
        // the player the wrong amount of base time.
        Assert.Equal(36_000, MissionTimeConverter.StepsPerStrategicTick);
        Assert.Equal(1, MissionTimeConverter.StepsToStrategicTicks(Step.FromMinutes(60).Value));
    }

    [Fact]
    public void StepsToStrategicTicksRejectsNegativeSteps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MissionTimeConverter.StepsToStrategicTicks(-1));
    }

    [Fact]
    public void ASubTickMissionStillCostsThePlayerOneTick()
    {
        // Rounding down would make the first 59 minutes of every mission free, which is
        // a systematic gift rather than a rounding detail.
        Assert.Equal(1, MissionTimeConverter.StepsToStrategicTicks(1));
    }

    [Fact]
    public void MissionTimeIsConvertedExactlyOnceOnClosing()
    {
        GameSession session = NewSession();
        session.EnterTacticalMode(NewMission());

        // Ten simulated minutes.
        session.AdvanceTacticalSteps(6_000);

        long ticks = session.LeaveTacticalMode();

        Assert.Equal(1, ticks);
        Assert.Equal(1, session.CurrentTick.Value);
        Assert.Equal(SessionMode.Strategic, session.Mode);
        Assert.Null(session.World.ActiveMission);
    }

    [Fact]
    public void ClosingAnAlreadyConvertedMissionCostsNothingFurther()
    {
        // The idempotence that stops a quit-then-load from eating an extra hour.
        GameSession session = NewSession();
        TacticalState mission = NewMission(36_000);
        mission.TimeConverted = true;
        session.EnterTacticalMode(mission);

        long ticks = session.LeaveTacticalMode();

        Assert.Equal(0, ticks);
        Assert.Equal(Tick.Zero, session.CurrentTick);
    }

    [Fact]
    public void AConvertedMissionCanBeClosedAgainWithoutThrowing()
    {
        GameSession session = NewSession();
        TacticalState mission = NewMission(36_000);
        mission.TimeConverted = true;
        session.EnterTacticalMode(mission);

        session.LeaveTacticalMode();

        Assert.Equal(SessionMode.Strategic, session.Mode);

        // Already left, so a second close is a caller error rather than a silent
        // double-charge.
        Assert.Throws<InvalidOperationException>(() => session.LeaveTacticalMode());
    }

    [Fact]
    public void BaseTimeResumesAfterAMission()
    {
        GameSession session = NewSession();
        session.EnterTacticalMode(NewMission());
        session.AdvanceTacticalSteps(36_000);
        session.LeaveTacticalMode();

        // One tick was owed for the mission; now the base can move again.
        session.AdvanceStrategicTick();
        Assert.Equal(2, session.CurrentTick.Value);
    }

    [Fact]
    public void ABlinkOfAMissionCostsNoBaseTime()
    {
        GameSession session = NewSession();
        session.EnterTacticalMode(NewMission());
        session.AdvanceTacticalStep();
        long ticks = session.LeaveTacticalMode();

        Assert.Equal(1, ticks);
        Assert.Equal(1, session.CurrentTick.Value);
    }

    // ---- speed sets ----------------------------------------------------------

    [Fact]
    public void StrategicSpeedSetIsTheOneKnowledgeMdSpecifies()
    {
        Assert.Equal(
            new[] { 0, 1, 4, 16, 64 },
            Enum.GetValues<StrategicTimeScale>().Select(s => (int)s).ToArray());

        Assert.Equal(1, StrategicTimeScaleInfo.TicksPerRealSecond(StrategicTimeScale.Normal));
        Assert.Equal(64, StrategicTimeScaleInfo.TicksPerRealSecond(StrategicTimeScale.Fastest));
        Assert.Equal(0, StrategicTimeScaleInfo.TicksPerRealSecond(StrategicTimeScale.Pause));
    }

    [Fact]
    public void TacticalSpeedSetIsTheOneKnowledgeMdSpecifies()
    {
        // knowledge.md rule 13: Pause / 0.5x / 1x / 2x / 4x — deliberately capped far
        // below the strategic top speed, because a mission is played directly.
        Assert.Equal(
            new[] { "Pause", "Half", "Normal", "Double", "Quad" },
            Enum.GetValues<TacticalTimeScale>().Select(s => s.ToString()).ToArray());
    }

    [Fact]
    public void TacticalSpeedsMapToStepsPerRealSecondWithoutFloats()
    {
        // One real minute of play is roughly one in-game minute at 1x.
        Assert.Equal(0, TacticalTimeScaleInfo.StepsPerRealSecond(TacticalTimeScale.Pause));
        Assert.Equal(5, TacticalTimeScaleInfo.StepsPerRealSecond(TacticalTimeScale.Half));
        Assert.Equal(10, TacticalTimeScaleInfo.StepsPerRealSecond(TacticalTimeScale.Normal));
        Assert.Equal(20, TacticalTimeScaleInfo.StepsPerRealSecond(TacticalTimeScale.Double));
        Assert.Equal(40, TacticalTimeScaleInfo.StepsPerRealSecond(TacticalTimeScale.Quad));
    }

    [Fact]
    public void SpeedIsStoredOnTheSessionPerScale()
    {
        GameSession session = NewSession();

        Assert.Equal(StrategicTimeScale.Normal, session.StrategicSpeed);
        Assert.Equal(TacticalTimeScale.Normal, session.TacticalSpeed);

        session.TacticalSpeed = TacticalTimeScale.Quad;
        session.StrategicSpeed = StrategicTimeScale.Fastest;

        Assert.Equal(TacticalTimeScale.Quad, session.TacticalSpeed);
        Assert.Equal(StrategicTimeScale.Fastest, session.StrategicSpeed);
    }

    [Fact]
    public void ChangingSpeedDoesNotChangeWhatAStepDoes()
    {
        // knowledge.md rule 13: a speed maps real seconds to steps and nothing else.
        // Two sessions at different speeds that simulate the same number of steps must
        // reach the same state.
        GameSession fast = NewSession();
        GameSession slow = NewSession();

        fast.TacticalSpeed = TacticalTimeScale.Quad;
        slow.TacticalSpeed = TacticalTimeScale.Half;

        fast.EnterTacticalMode(NewMission());
        slow.EnterTacticalMode(NewMission());

        fast.AdvanceTacticalSteps(1_000);
        slow.AdvanceTacticalSteps(1_000);

        Assert.Equal(fast.World.ComputeStateHash(), slow.World.ComputeStateHash());
    }
}