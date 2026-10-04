using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Unity.Simulation;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves a speed is a clock and not a rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// knowledge.md rule 13 says a speed control "must NEVER change simulation results" —
    /// it may only change how real seconds map to ticks or steps. These tests pin the half
    /// of that which lives in Presentation: that the mapping is the documented one, that
    /// pausing suppresses calls entirely, and that a fractional remainder carries across
    /// frames instead of being rounded away.
    /// </para>
    /// <para>
    /// That pausing does not alter <em>what</em> Advance does is a Core property and is
    /// tested there; what is tested here is that pause never reaches Core at all, which is
    /// what makes that Core property hold in practice.
    /// </para>
    /// </remarks>
    public sealed class TimeControllerTests
    {
        [Test]
        public void StrategicSpeedsProduceTheDocumentedTicksPerSecond()
        {
            Assert.That(Consumed(new StrategicTimeController(StrategicTimeScale.Pause), 1f), Is.Zero);
            Assert.That(Consumed(new StrategicTimeController(StrategicTimeScale.Normal), 1f), Is.EqualTo(1));
            Assert.That(Consumed(new StrategicTimeController(StrategicTimeScale.Fast), 1f), Is.EqualTo(4));
            Assert.That(Consumed(new StrategicTimeController(StrategicTimeScale.Faster), 1f), Is.EqualTo(16));
            Assert.That(Consumed(new StrategicTimeController(StrategicTimeScale.Fastest), 1f), Is.EqualTo(64));
        }

        [Test]
        public void TacticalSpeedsProduceTenStepsPerSecondAtOneTimes()
        {
            // A step is 100ms, so 1x is ten steps per real second.
            Assert.That(Consumed(new TacticalTimeController(TacticalTimeScale.Pause), 1f), Is.Zero);
            Assert.That(Consumed(new TacticalTimeController(TacticalTimeScale.Half), 1f), Is.EqualTo(5));
            Assert.That(Consumed(new TacticalTimeController(TacticalTimeScale.Normal), 1f), Is.EqualTo(10));
            Assert.That(Consumed(new TacticalTimeController(TacticalTimeScale.Double), 1f), Is.EqualTo(20));
            Assert.That(Consumed(new TacticalTimeController(TacticalTimeScale.Quad), 1f), Is.EqualTo(40));
        }

        [Test]
        public void PausingAdvancesNothingAtAll()
        {
            var strategic = new StrategicTimeController(StrategicTimeScale.Pause);
            var tactical = new TacticalTimeController(TacticalTimeScale.Pause);

            Assert.That(strategic.IsPaused, Is.True);
            Assert.That(tactical.IsPaused, Is.True);

            for (int i = 0; i < 100; i++)
            {
                Assert.That(strategic.Consume(0.016f), Is.Zero);
                Assert.That(tactical.Consume(0.016f), Is.Zero);
            }
        }

        [Test]
        public void AFractionalRemainderCarriesAcrossFrames()
        {
            // Half speed is half a tick per second. A controller that rounded per frame
            // would advance zero ticks, forever, and the clock would simply stop.
            var controller = new StrategicTimeController(StrategicTimeScale.Normal);

            int total = 0;
            for (int i = 0; i < 100; i++)
                total += controller.Consume(0.005f);

            Assert.That(total, Is.EqualTo(1), "half a second of frames should yield exactly one tick");
        }

        [Test]
        public void ManySmallFramesSumToTheSameTicksAsOneBigOne()
        {
            var controller = new TacticalTimeController(TacticalTimeScale.Normal);

            int accumulated = 0;
            for (int i = 0; i < 50; i++)
                accumulated += controller.Consume(0.02f);

            Assert.That(accumulated, Is.EqualTo(TacticalTimeScaleInfo.StepsPerRealSecond(TacticalTimeScale.Normal)));
        }

        [Test]
        public void ResettingTheAccumulatorDropsTheRemainder()
        {
            var controller = new TacticalTimeController(TacticalTimeScale.Half);

            controller.Consume(0.005f); // 0.025 steps banked
            controller.ResetAccumulator();

            // With the remainder dropped, a single short frame cannot produce a step.
            Assert.That(controller.Consume(0.01f), Is.Zero);
        }

        [Test]
        public void CyclingSpeedWalksTheWholeSetAndReturns()
        {
            var controller = new StrategicTimeController(StrategicTimeScale.Pause);
            var seen = new System.Collections.Generic.List<StrategicTimeScale> { controller.Speed };

            for (int i = 0; i < 4; i++)
            {
                controller.CycleSpeed();
                seen.Add(controller.Speed);
            }

            CollectionAssert.AreEqual(
                new[]
                {
                    StrategicTimeScale.Pause, StrategicTimeScale.Normal, StrategicTimeScale.Fast,
                    StrategicTimeScale.Faster, StrategicTimeScale.Fastest,
                },
                seen);
        }

        /// <summary>Consumes one second as a single frame and returns the units produced.</summary>
        private static int Consumed(TimeController controller, float seconds)
        {
            controller.ResetAccumulator();
            return controller.Consume(seconds);
        }
    }
}