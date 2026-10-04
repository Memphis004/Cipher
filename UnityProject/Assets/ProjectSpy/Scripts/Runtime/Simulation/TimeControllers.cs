using ProjectSpy.Core;
using UnityEngine;

namespace ProjectSpy.Unity.Simulation
{
    /// <summary>
    /// Turns real seconds into Core ticks or steps, and never anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of the speed system's job, and the discipline is the point
    /// (knowledge.md rule 13). A speed is a mapping from real seconds to ticks. It must not
    /// change what a tick does, so there is no branch anywhere below that alters the value
    /// passed to Core, the order of calls, or the number of systems that run. At 64x this
    /// calls <c>AdvanceTick</c> sixty-four times in one frame and gets the same sixty-four
    /// ticks a one-minute wait would have produced.
    /// </para>
    /// <para>
    /// <b>Integer accumulation.</b> The leftover from one frame carries into the next, so
    /// at 0.5x — half a tick per second — the clock still advances exactly one tick every
    /// two seconds rather than rounding to zero forever. A float accumulator would also work
    /// but would make the mapping depend on frame timing in a way that is very hard to
    /// reproduce; this stays integral all the way to the call.
    /// </para>
    /// <para>
    /// <b>Pausing stops the calls.</b> <see cref="IsPaused"/> suppresses them entirely and
    /// does not reach Core. Core is not told a pause happened, which is what guarantees that
    /// unpausing cannot perturb the simulation.
    /// </para>
    /// </remarks>
    public abstract class TimeController
    {
        /// <summary>Whether the clock is stopped. While true, Advance is not called at all.</summary>
        public abstract bool IsPaused { get; }

        /// <summary>
        /// Consumes <paramref name="realSeconds"/> and returns how many Core units to
        /// advance this frame.
        /// </summary>
        /// <remarks>
        /// Returns zero while paused. The caller is expected to make exactly that many
        /// calls to Core and nothing more.
        /// </remarks>
        public abstract int Consume(float realSeconds);

        /// <summary>
        /// Clears the fractional accumulator.
        /// </summary>
        /// <remarks>
        /// Called on load. Without it, a save loaded mid-frame inherits whatever sub-unit
        /// remainder the previous run had, and the first tick after a load lands at a
        /// slightly different point than it did before — which is the kind of thing that
        /// shows up much later as an unexplainable replay divergence.
        /// </remarks>
        public abstract void ResetAccumulator();
    }

    /// <summary>
    /// Real seconds to strategic ticks, at Pause / 1x / 4x / 16x / 64x.
    /// </summary>
    /// <remarks>
    /// One tick is one in-game hour, so at 64x a full day passes in under half a second.
    /// The controller holds no clock state of its own: the tick count lives in
    /// <see cref="StrategicClock"/> inside Core, and this only decides how many to ask for.
    /// </remarks>
    public sealed class StrategicTimeController : TimeController
    {
        private double _accumulatedSeconds;
        private int _ticksPerSecond;

        /// <summary>Creates a controller at the given speed.</summary>
        public StrategicTimeController(StrategicTimeScale speed = StrategicTimeScale.Normal)
        {
            SetSpeed(speed);
        }

        /// <inheritdoc/>
        public override bool IsPaused => _ticksPerSecond == 0;

        /// <summary>The current speed.</summary>
        public StrategicTimeScale Speed { get; private set; }

        /// <summary>
        /// Changes speed. Resets nothing: the fractional remainder carries over, so
        /// switching from 1x to 4x does not lose the part of a tick already banked.
        /// </summary>
        public void SetSpeed(StrategicTimeScale speed)
        {
            Speed = speed;
            _ticksPerSecond = StrategicTimeScaleInfo.TicksPerRealSecond(speed);
        }

        /// <summary>Steps through the five strategic speeds in order.</summary>
        public void CycleSpeed()
        {
            var order = new[]
            {
                StrategicTimeScale.Pause, StrategicTimeScale.Normal, StrategicTimeScale.Fast,
                StrategicTimeScale.Faster, StrategicTimeScale.Fastest,
            };
            int index = System.Array.IndexOf(order, Speed);
            SetSpeed(order[(index + 1) % order.Length]);
        }

        /// <inheritdoc/>
        public override int Consume(float realSeconds)
        {
            if (IsPaused)
                return 0;

            _accumulatedSeconds += realSeconds;

            // Whole ticks only. The cast truncates, and the remainder stays banked.
            int whole = (int)_accumulatedSeconds;
            if (whole == 0)
                return 0;

            _accumulatedSeconds -= whole;
            return whole * _ticksPerSecond;
        }

        /// <inheritdoc/>
        public override void ResetAccumulator() => _accumulatedSeconds = 0d;
    }

    /// <summary>
    /// Real seconds to tactical steps, at Pause / 0.5x / 1x / 2x / 4x.
    /// </summary>
    /// <remarks>
    /// A step is 100ms of simulated time, so at 1x this produces exactly ten steps per
    /// real second. The speed set is deliberately different from the strategic one and
    /// capped far lower: a mission is played directly and a player who needs 16x to cross
    /// a room is not reading the light levels.
    /// </remarks>
    public sealed class TacticalTimeController : TimeController
    {
        /// <summary>Steps per simulated second, from Core.</summary>
        public const int StepsPerSecond = Step.StepsPerSecond;

        private double _accumulatedSteps;

        /// <summary>Creates a controller at the given speed.</summary>
        public TacticalTimeController(TacticalTimeScale speed = TacticalTimeScale.Normal)
        {
            SetSpeed(speed);
        }

        /// <inheritdoc/>
        public override bool IsPaused => Speed == TacticalTimeScale.Pause;

        /// <summary>The current speed.</summary>
        public TacticalTimeScale Speed { get; private set; }

        /// <summary>Changes speed, keeping the banked sub-step remainder.</summary>
        public void SetSpeed(TacticalTimeScale speed) => Speed = speed;

        /// <summary>Steps through the five tactical speeds in order.</summary>
        public void CycleSpeed()
        {
            var order = new[]
            {
                TacticalTimeScale.Pause, TacticalTimeScale.Half, TacticalTimeScale.Normal,
                TacticalTimeScale.Double, TacticalTimeScale.Quad,
            };
            int index = System.Array.IndexOf(order, Speed);
            SetSpeed(order[(index + 1) % order.Length]);
        }

        /// <inheritdoc/>
        public override int Consume(float realSeconds)
        {
            if (IsPaused)
                return 0;

            // Accumulated in double because a step is 100ms: at 0.5x a frame advances
            // 0.005 steps, and float would drift over a long mission.
            _accumulatedSteps += realSeconds * TacticalTimeScaleInfo.StepsPerRealSecond(Speed);

            int whole = (int)_accumulatedSteps;
            if (whole == 0)
                return 0;

            _accumulatedSteps -= whole;
            return whole;
        }

        /// <inheritdoc/>
        public override void ResetAccumulator() => _accumulatedSteps = 0d;
    }
}