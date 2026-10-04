namespace ProjectSpy.Core;

/// <summary>
/// Speed settings for the strategic clock.
/// </summary>
/// <remarks>
/// <para>
/// A speed is a mapping from real seconds to ticks and nothing else. It never changes
/// what a tick does, only how fast ticks arrive (knowledge.md rule 13). That is why the
/// multiplier is stored as an integer numerator and denominator rather than a float:
/// a speed belongs to the presentation layer, but the mapping it implies is a rule
/// about how long a week takes, so it must not be able to differ by a rounding error
/// between two runs of the balance simulator.
/// </para>
/// <para>
/// The set is Pause / 1x / 4x / 16x / 64x, so a week passes in seconds at the top end.
/// </para>
/// </remarks>
public enum StrategicTimeScale
{
    /// <summary>Paused. Time does not advance.</summary>
    Pause = 0,

    /// <summary>1x — the reference speed.</summary>
    Normal = 1,

    /// <summary>4x.</summary>
    Fast = 4,

    /// <summary>16x.</summary>
    Faster = 16,

    /// <summary>64x. A day passes in well under a second.</summary>
    Fastest = 64,
}

/// <summary>Multipliers for <see cref="StrategicTimeScale"/>.</summary>
public static class StrategicTimeScaleInfo
{
    /// <summary>How many in-game hours one real second represents at this speed.</summary>
    public static int TicksPerRealSecond(StrategicTimeScale speed) => (int)speed;
}

/// <summary>
/// Speed settings for the tactical clock.
/// </summary>
/// <remarks>
/// <para>
/// The tactical set is deliberately different from the strategic one and deliberately
/// capped much lower: Pause / 0.5x / 1x / 2x / 4x. A tactical mission is played
/// directly, one agent at a time, and a player who needs 16x to cross a room is not
/// reading the light levels. At 1x one real minute of play is roughly one in-game
/// minute, so the tactical speed reads in real time at its reference setting.
/// </para>
/// <para>
/// As with the strategic speed, a speed only maps real seconds to steps. It never
/// changes a step's result (knowledge.md rule 13).
/// </para>
/// </remarks>
public enum TacticalTimeScale
{
    /// <summary>Paused. Time does not advance.</summary>
    Pause = 0,

    /// <summary>0.5x — half speed.</summary>
    Half = 1,

    /// <summary>1x — real time.</summary>
    Normal = 2,

    /// <summary>2x.</summary>
    Double = 3,

    /// <summary>4x.</summary>
    Quad = 4,
}

/// <summary>Multipliers for <see cref="TacticalTimeScale"/>, as integer fractions.</summary>
public static class TacticalTimeScaleInfo
{
    /// <summary>The speed used as the 1x reference.</summary>
    public const TacticalTimeScale Reference = TacticalTimeScale.Normal;

    /// <summary>Numerator of the speed multiplier, in halves. 2 means 1x.</summary>
    public static int MultiplierNumerator(TacticalTimeScale speed) => speed switch
    {
        TacticalTimeScale.Pause => 0,
        TacticalTimeScale.Half => 1,
        TacticalTimeScale.Normal => 2,
        TacticalTimeScale.Double => 4,
        TacticalTimeScale.Quad => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, "Unknown tactical speed."),
    };

    /// <summary>Denominator of the speed multiplier. Always 2, so halves stay exact.</summary>
    public const int MultiplierDenominator = 2;

    /// <summary>Steps per real second at this speed.</summary>
    public static int StepsPerRealSecond(TacticalTimeScale speed)
        => Step.StepsPerSecond * MultiplierNumerator(speed) / MultiplierDenominator;
}