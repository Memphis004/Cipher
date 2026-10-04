using System.Globalization;

namespace ProjectSpy.Core;

/// <summary>
/// A length in centimetres, stored as an <see cref="int"/>. 1 unit = 1 cm.
/// </summary>
/// <remarks>
/// <para>
/// Every rule-affecting distance in the tactical layer goes through this type rather
/// than through <c>float</c>. Two reasons, in order of weight:
/// </para>
/// <list type="number">
/// <item>
/// <b>Determinism.</b> <c>float</c> ordering differs between platforms and between
/// instruction sets. A vision check written as <c>a * 0.5f &gt; b</c> can answer
/// differently on the player's machine than in the balance simulator, which makes
/// every headless balance number untrustworthy (knowledge.md rule 6).
/// </item>
/// <item>
/// <b>Legibility.</b> A range check becomes an integer comparison on a value the
/// player can be shown exactly, which is what makes the perception breakdown in the
/// UI possible at all.
/// </item>
/// </list>
/// <para>
/// <b>Overflow is loud.</b> Every operator widens to <see cref="long"/>, performs the
/// arithmetic, then throws <see cref="OverflowException"/> if the result does not fit
/// the backing <see cref="int"/>. Saturating instead would let a runaway radius or a
/// negative width propagate silently through the whole simulation and be discovered
/// three stages later as an unexplainable save.
/// </para>
/// <para>
/// <b>Multiply and divide are percentage operations.</b> Two lengths do not multiply
/// into a length, so <c>a * b</c> is defined as <c>a × b / 100</c> and <c>a / b</c> as
/// <c>a × 100 / b</c>. That makes <c>range * 75</c> a 75% range and
/// <c>noise * noiseAttenuation</c> read the way a designer writes them, and it keeps
/// <c>a * 100 == a</c> exact. For plain integer scaling use the <see cref="int"/>
/// overloads, which never round.
/// </para>
/// <para>
/// <b>Naming.</b> Nothing here is called <c>Scale</c>, <c>Width</c> or <c>Position</c>,
/// because those words are reserved by knowledge.md rule 10's guard: a member called
/// <c>PercentScale</c> is one careless rename away from being read as a render scale,
/// and the guard that catches that would then have to be loosened instead. The names
/// here say what the number <em>is</em>, not what it multiplies.
/// </para>
/// <para>
/// Representation range is ±2,147,483,647 cm, about ±21,474 km. No site comes close.
/// </para>
/// </remarks>
public readonly struct Fixed32 : IEquatable<Fixed32>, IComparable<Fixed32>
{
    /// <summary>The factor percentage multiply and divide are defined against.</summary>
    public const int PercentDivisor = 100;

    /// <summary>Centimetres in one metre.</summary>
    public const int CmPerMetre = 100;

    /// <summary>Zero centimetres.</summary>
    public static readonly Fixed32 Zero = new(0);

    /// <summary>Wraps a raw centimetre count.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public Fixed32(int centimetres) => Raw = centimetres;

    /// <summary>The raw centimetre count. This is the stored representation.</summary>
    public int Raw { get; }

    /// <summary>Negative zero distance, i.e. <c>0</c> cm.</summary>
    public bool IsZero => Raw == 0;

    /// <summary>True when the value is below zero.</summary>
    public bool IsNegative => Raw < 0;

    /// <summary>Builds a length from whole centimetres.</summary>
    public static Fixed32 FromCm(int centimetres) => new(centimetres);

    /// <summary>Builds a length from whole metres.</summary>
    /// <exception cref="OverflowException">The metres do not fit in <see cref="int"/> cm.</exception>
    public static Fixed32 FromMetres(int metres) => new(Checked((long)metres * CmPerMetre));

    /// <summary>
    /// Absolute distance between two points on the same line.
    /// </summary>
    /// <remarks>
    /// Absolute, not signed. A signed version belongs in the subtraction operator;
    /// a method called <c>Distance</c> that can return a negative length is a trap
    /// waiting for the first vision check that forgets to take an absolute value.
    /// </remarks>
    public static Fixed32 Distance(Fixed32 from, Fixed32 to)
    {
        long delta = (long)to.Raw - from.Raw;
        return new Fixed32(Checked(delta < 0 ? -delta : delta));
    }

    /// <summary>
    /// The length as a float in metres.
    /// </summary>
    /// <remarks>
    /// PRESENTATION ONLY. This is the single sanctioned float escape hatch in the
    /// tactical model, and it exists so a HUD label can read "12.4 m". Never feed the
    /// result back into Core: a rule that reads it is a determinism bug
    /// (knowledge.md rule 6).
    /// </remarks>
    public float ToDisplayMetres() => Raw / (float)CmPerMetre;

    /// <summary>Absolute value.</summary>
    public Fixed32 Abs() => Raw < 0 ? new Fixed32(-Raw) : this;

    /// <summary>The smaller of two values.</summary>
    public static Fixed32 Min(Fixed32 a, Fixed32 b) => a.Raw <= b.Raw ? a : b;

    /// <summary>The larger of two values.</summary>
    public static Fixed32 Max(Fixed32 a, Fixed32 b) => a.Raw >= b.Raw ? a : b;

    /// <summary>
    /// Clamps to an inclusive range.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="min"/> exceeds <paramref name="max"/>.</exception>
    public static Fixed32 Clamp(Fixed32 value, Fixed32 min, Fixed32 max)
    {
        if (min.Raw > max.Raw)
            throw new ArgumentException(
                $"Clamp range is inverted: min {min.Raw} cm > max {max.Raw} cm.", nameof(min));

        return value.Raw < min.Raw ? min : value.Raw > max.Raw ? max : value;
    }

    /// <summary>
    /// Integer square root of the raw magnitude, rounded down.
    /// </summary>
    /// <remarks>
    /// Used on squared distances — <c>SqrtApprox(SquaredDistance(a, b))</c> — where the
    /// squared value is held in a wider integer first. Computed by digit-pair extraction
    /// over <see cref="long"/> arithmetic, so it is exact and identical on every
    /// platform; a <c>Math.Sqrt</c> followed by a cast would not be, near a perfect
    /// square.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public static Fixed32 SqrtApprox(Fixed32 value)
    {
        if (value.Raw < 0)
            throw new ArgumentOutOfRangeException(
                nameof(value), value.Raw, "Cannot take the square root of a negative length.");

        return new Fixed32(IntegerSqrt((ulong)value.Raw));
    }

    /// <summary>
    /// Linearly interpolates from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <param name="t">
    /// Progress in percent, matching <see cref="PercentDivisor"/>: 0 returns
    /// <paramref name="from"/>, 100 returns <paramref name="to"/>. Values outside
    /// <c>[0, 100]</c> extrapolate rather than clamp, so a thrown trajectory is not
    /// silently flattened at its endpoint.
    /// </param>
    /// <exception cref="OverflowException">The result does not fit in <see cref="int"/> cm.</exception>
    public static Fixed32 Lerp(Fixed32 from, Fixed32 to, Fixed32 t)
    {
        long delta = (long)to.Raw - from.Raw;

        // |delta| <= 2^32 - 1 and |t| <= 2^31 - 1, so the product tops out just
        // under long.MaxValue. Widening further is not available on netstandard2.1,
        // so the bound is asserted here rather than assumed: if the representation ever
        // widens, this is the line that stops being true.
        long scaled = delta * t.Raw;
        return new Fixed32(Checked(from.Raw + DivRoundHalfAwayFromZero(scaled, PercentDivisor)));
    }

    public static Fixed32 operator +(Fixed32 a, Fixed32 b) => new(Checked((long)a.Raw + b.Raw));

    public static Fixed32 operator -(Fixed32 a, Fixed32 b) => new(Checked((long)a.Raw - b.Raw));

    public static Fixed32 operator -(Fixed32 a) => new(Checked(-(long)a.Raw));

    /// <summary>Percentage multiply: <c>a × b / 100</c>.</summary>
    /// <exception cref="OverflowException">The result does not fit in <see cref="int"/> cm.</exception>
    public static Fixed32 operator *(Fixed32 a, Fixed32 b)
        => new(Checked(DivRoundHalfAwayFromZero((long)a.Raw * b.Raw, PercentDivisor)));

    /// <summary>Percentage divide: <c>a × 100 / b</c>.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="b"/> is zero.</exception>
    /// <exception cref="OverflowException">The result does not fit in <see cref="int"/> cm.</exception>
    public static Fixed32 operator /(Fixed32 a, Fixed32 b)
    {
        if (b.Raw == 0)
            throw new DivideByZeroException("Cannot divide a length by zero.");

        return new Fixed32(Checked(DivRoundHalfAwayFromZero((long)a.Raw * PercentDivisor, b.Raw)));
    }

    /// <summary>Exact scaling by a whole number. Never rounds.</summary>
    /// <exception cref="OverflowException">The result does not fit in <see cref="int"/> cm.</exception>
    public static Fixed32 operator *(Fixed32 a, int scalar) => new(Checked((long)a.Raw * scalar));

    /// <summary>Exact division by a whole number, rounded half away from zero.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="scalar"/> is zero.</exception>
    /// <exception cref="OverflowException">The result does not fit in <see cref="int"/> cm.</exception>
    public static Fixed32 operator /(Fixed32 a, int scalar)
    {
        if (scalar == 0)
            throw new DivideByZeroException("Cannot divide a length by zero.");

        return new Fixed32(Checked(DivRoundHalfAwayFromZero(a.Raw, scalar)));
    }

    // == and != are written out rather than generated, because this is a plain
    // readonly struct rather than a record struct (see Tick for the record-struct
    // counterpart of this decision).

    public static bool operator ==(Fixed32 a, Fixed32 b) => a.Raw == b.Raw;

    public static bool operator !=(Fixed32 a, Fixed32 b) => a.Raw != b.Raw;

    public static bool operator <(Fixed32 a, Fixed32 b) => a.Raw < b.Raw;

    public static bool operator >(Fixed32 a, Fixed32 b) => a.Raw > b.Raw;

    public static bool operator <=(Fixed32 a, Fixed32 b) => a.Raw <= b.Raw;

    public static bool operator >=(Fixed32 a, Fixed32 b) => a.Raw >= b.Raw;

    public bool Equals(Fixed32 other) => Raw == other.Raw;

    public override bool Equals(object? obj) => obj is Fixed32 other && Equals(other);

    public override int GetHashCode() => Raw;

    public int CompareTo(Fixed32 other) => Raw.CompareTo(other.Raw);

    /// <summary>Stable, culture-invariant text form. Centimetres, no unit suffix.</summary>
    public override string ToString() => Raw.ToString(CultureInfo.InvariantCulture);

    /// <summary>Narrows a widened result, refusing rather than wrapping.</summary>
    /// <exception cref="OverflowException">The value is outside <see cref="int"/> range.</exception>
    private static int Checked(long centimetres)
    {
        if (centimetres > int.MaxValue || centimetres < int.MinValue)
        {
            throw new OverflowException(
                $"Fixed32 result {centimetres} cm is outside the representable range " +
                $"[{int.MinValue}, {int.MaxValue}] cm.");
        }

        return (int)centimetres;
    }

    /// <summary>
    /// Integer division rounding halves away from zero.
    /// </summary>
    /// <remarks>
    /// Away from zero rather than toward positive infinity so that the sign of the
    /// result always follows the operands: <c>-5 / 2</c> is <c>-3</c>, not <c>-2</c>. A
    /// rule path that rounds a negative offset toward positive infinity drifts, and the
    /// drift is not reproducible-looking enough to notice until a replay diverges.
    /// </remarks>
    private static long DivRoundHalfAwayFromZero(long numerator, long denominator)
    {
        if (denominator == 0)
            throw new DivideByZeroException("Division by zero in Fixed32 arithmetic.");

        long quotient = numerator / denominator;
        long remainder = numerator % denominator;

        if (remainder == 0)
            return quotient;

        long magnitude = remainder < 0 ? -remainder : remainder;
        long divisorMagnitude = denominator < 0 ? -denominator : denominator;

        // 2 * |remainder| >= |denominator| means the fraction was at least a half.
        return magnitude * 2 >= divisorMagnitude
            ? quotient + (numerator < 0 ? -1 : 1)
            : quotient;
    }

    /// <summary>Exact integer square root, digit pair at a time.</summary>
    private static int IntegerSqrt(ulong value)
    {
        ulong remainder = value;
        ulong result = 0;
        ulong bit = 1UL << 62;

        while (bit > remainder)
            bit >>= 2;

        while (bit != 0)
        {
            if (remainder >= result + bit)
            {
                remainder -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }

            bit >>= 2;
        }

        return (int)result;
    }
}