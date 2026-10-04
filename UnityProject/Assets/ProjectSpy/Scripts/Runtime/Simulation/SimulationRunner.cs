using System;
using ProjectSpy.Core;
using R3;
using UnityEngine;

namespace ProjectSpy.Unity.Simulation
{
    /// <summary>
    /// Drives Core's clocks from real time, and republishes Core's events onto an R3
    /// subject for the UI to bind to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This component owns no simulation state.</b> It owns a <see cref="GameSession"/>,
    /// asks it to advance, and forwards what comes back. Everything that decides what a tick
    /// means lives in Core; everything here is a clock and a pipe.
    /// </para>
    /// <para>
    /// <b>Speed changes how many calls happen, never what a call does</b> (rule 13). There is
    /// deliberately no branch on speed inside the advance loop. At 64x this calls
    /// <c>AdvanceTick</c> sixty-four times in one frame and gets the same sixty-four ticks
    /// that a minute of real waiting would have produced.
    /// </para>
    /// <para>
    /// <b>Pausing stops calling Advance and nothing else.</b> Core is never told a pause
    /// happened. That is what makes "pause, issue orders, resume" safe: the simulation's
    /// behaviour on resume is identical to a pause that never occurred, because as far as
    /// Core is concerned it did.
    /// </para>
    /// <para>
    /// <b>Interpolation is presentation.</b> <see cref="RenderAlpha"/> reports how far the
    /// renderer should sit between the last two simulated states. Nothing in Core reads it,
    /// and nothing here writes a position back into the simulation. A replays are therefore
    /// unaffected by frame rate, which is the property that makes them worth having.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SimulationRunner : MonoBehaviour
    {
        [SerializeField, Tooltip("Seed for a fresh world. Ignored when a session is injected.")]
        private ulong _seed = 20251004UL;

        private GameSession _session;
        private IDisposable _coreSubscription;
        private StrategicTimeController _strategic;
        private TacticalTimeController _tactical;

        // Fraction of the way from the last simulated state to the next, for rendering.
        private float _alpha;
        private int _unitsAdvancedLastFrame;

        /// <summary>
        /// Core events, republished for the UI.
        /// </summary>
        /// <remarks>
        /// An <see cref="Subject{T}"/> rather than exposing Core's own stream, so that
        /// Presentation code cannot accidentally reach Core's subscription list and depend on
        /// its shape. This is the seam knowledge.md rule 8 asks for: the UI binds to an R3
        /// stream and never to Core directly.
        /// </remarks>
        public Subject<GameEvent> Events { get; } = new();

        /// <summary>The session being driven.</summary>
        public GameSession Session => _session;

        /// <summary>Ticks or steps advanced during the last Update.</summary>
        public int UnitsAdvancedLastFrame => _unitsAdvancedLastFrame;

        /// <summary>Total ticks or steps advanced since this runner started.</summary>
        public long TotalUnitsAdvanced { get; private set; }

        /// <summary>
        /// How far between the last two simulated states the renderer should draw.
        /// </summary>
        /// <remarks>
        /// Zero immediately after an advance, rising back toward one as the next one
        /// approaches. Strictly a rendering concern — nothing in Core may read it, which is
        /// the only way "the simulation never interpolates" stays true rather than being an
        /// intention.
        /// </remarks>
        public float RenderAlpha => _alpha;

        /// <summary>True while the strategic clock is stopped.</summary>
        public bool IsStrategicPaused => _strategic?.IsPaused ?? true;

        /// <summary>True while the tactical clock is stopped.</summary>
        public bool IsTacticalPaused => _tactical?.IsPaused ?? true;

        /// <summary>The active mode.</summary>
        public SessionMode Mode => _session?.Mode ?? SessionMode.Strategic;

        /// <summary>
        /// Builds a session and starts driving it.
        /// </summary>
        /// <remarks>
        /// Called automatically on Awake so a scene with this component and nothing else
        /// still simulates. <see cref="UseSession"/> exists for tests and for loading a
        /// save, where the session already exists and must not be replaced.
        /// </remarks>
        private void Awake()
        {
            if (_session is null)
                _session = new GameSession(_seed);

            Initialise();
        }

        private void OnDestroy()
        {
            _coreSubscription?.Dispose();
            _coreSubscription = null;
            Events.Dispose();
        }

        /// <summary>
        /// Drives an externally supplied session, e.g. one restored from a save.
        /// </summary>
        public void UseSession(GameSession session)
        {
            if (session is null)
                throw new ArgumentNullException(nameof(session));

            _session = session;
            Initialise();
        }

        private void Initialise()
        {
            _coreSubscription?.Dispose();

            _strategic = new StrategicTimeController(_session.StrategicSpeed);
            _tactical = new TacticalTimeController(_session.TacticalSpeed);

            // Core's stream is synchronous and in publish order; forwarding it as-is keeps
            // that. A UI subscriber that throws will surface at the point of publication
            // rather than being swallowed, which during determinism work is much cheaper
            // than a quiet divergence three hours later.
            _coreSubscription = _session.Subscribe(Events.OnNext);
        }

        /// <summary>Sets the strategic speed.</summary>
        public void SetStrategicSpeed(StrategicTimeScale speed)
        {
            _session.StrategicSpeed = speed;
            _strategic.SetSpeed(speed);
        }

        /// <summary>Sets the tactical speed.</summary>
        public void SetTacticalSpeed(TacticalTimeScale speed)
        {
            _session.TacticalSpeed = speed;
            _tactical.SetSpeed(speed);
        }

        /// <summary>
        /// Advances the simulation to match real time.
        /// </summary>
        /// <remarks>
        /// Uses unscaled delta time so that pausing the game via <c>Time.timeScale</c> does
        /// not also pause the simulation. The simulation's pause is this class's own, and
        /// conflating the two would make the two-scale clock untestable.
        /// </remarks>
        private void Update()
        {
            _unitsAdvancedLastFrame = 0;

            // Real seconds since the previous frame. Unscaled, because the simulation has
            // its own pause and must not inherit Unity's.
            float realSeconds = Time.unscaledDeltaTime;

            switch (_session.Mode)
            {
                case SessionMode.Strategic:
                    _unitsAdvancedLastFrame = _strategic.Consume(realSeconds);
                    for (int i = 0; i < _unitsAdvancedLastFrame; i++)
                        _session.AdvanceTick();
                    break;

                case SessionMode.Tactical:
                    _unitsAdvancedLastFrame = _tactical.Consume(realSeconds);
                    for (int i = 0; i < _unitsAdvancedLastFrame; i++)
                        _session.AdvanceTacticalStep();
                    break;
            }

            TotalUnitsAdvanced += _unitsAdvancedLastFrame;
            UpdateAlpha(realSeconds);
        }

        /// <summary>
        /// Recomputes the render interpolation factor.
        /// </summary>
        /// <remarks>
        /// A full unit's worth of real time is the span over which the renderer slides from
        /// one simulated state to the next. The fraction already consumed this frame is
        /// subtracted, so an entity drawn at <c>RenderAlpha</c> sits where the simulation
        /// will be once the pending steps have run.
        /// </remarks>
        private void UpdateAlpha(float realSeconds)
        {
            float unitsPerSecond = _session.Mode == SessionMode.Tactical
                ? TacticalTimeScaleInfo.StepsPerRealSecond(_tactical.Speed)
                : StrategicTimeScaleInfo.TicksPerRealSecond(_session.StrategicSpeed);

            if (unitsPerSecond <= 0)
            {
                // Paused: hold the last state rather than sliding anywhere.
                _alpha = 0f;
                return;
            }

            float unitSeconds = 1f / unitsPerSecond;
            _alpha += realSeconds / unitSeconds;

            // More than one unit's worth of real time can pass in a single frame at high
            // speed. Wrapping keeps the fraction meaningful instead of running away, and
            // matches the fact that the states already advanced past have been consumed.
            while (_alpha >= 1f)
                _alpha -= 1f;
        }

        /// <summary>
        /// Clears the interpolation remainder, e.g. after loading a save.
        /// </summary>
        public void ResetInterpolation()
        {
            _alpha = 0f;
            _strategic?.ResetAccumulator();
            _tactical?.ResetAccumulator();
        }
    }
}