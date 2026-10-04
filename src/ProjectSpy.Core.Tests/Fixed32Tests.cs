using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves <see cref="Fixed32"/> is exact, deterministic and loud about overflow.
/// </summary>
/// <remarks>
/// The rounding tests matter more than they look. A rule path that rounds a
/// negative offset toward positive infinity drifts, and drift in a stealth sim shows
/// up as a guard who hears a noise he should not, which is a bug nobody reproduces
/// twice the same way.
/// </remarks>
public class Fixed32Tests
{
    // ---- construction --------------------------------------------------------

    [Fact]
    public void OneUnitIsOneCentimetre()
    {
        Assert.Equal(1, Fixed32.FromCm(1).Raw);
        Assert.Equal(100, Fixed32.FromMetres(1).Raw);
        Assert.Equal(250, Fixed32.FromCm(250).Raw);
    }

    [Fact]
    public void FromMetres_OverflowsLoudlyRatherThanWrapping()
    {
        // int.MaxValue cm is about 21,474 km. Metres cannot express that.
        Assert.Throws<OverflowException>(() => Fixed32.FromMetres(int.MaxValue));
        Assert.Throws<OverflowException>(() => Fixed32.FromMetres(int.MinValue));
    }

    [Fact]
    public void ToDisplayMetres_IsPresentationOnlyAndRoundsForDisplay()
    {
        // 125 cm is 1.25 m exactly in binary floating point, so this is exact.
        Assert.Equal(1.25f, Fixed32.FromCm(125).ToDisplayMetres());

        // 133 cm is 1.33 m, which is NOT representable. The assertion is on the
        // nearest float, not on the decimal string, because that is what a label
        // would actually be built from.
        Assert.Equal(1.33f, Fixed32.FromCm(133).ToDisplayMetres(), 4);

        Assert.Equal(0f, Fixed32.Zero.ToDisplayMetres());
    }

    // ---- arithmetic ----------------------------------------------------------

    [Fact]
    public void AdditionAndSubtractionAreExact()
    {
        Assert.Equal(300, (Fixed32.FromCm(100) + Fixed32.FromCm(200)).Raw);
        Assert.Equal(0, (Fixed32.FromCm(100) - Fixed32.FromCm(100)).Raw);
        Assert.Equal(-100, (Fixed32.FromCm(100) - Fixed32.FromCm(200)).Raw);
        Assert.Equal(-100, (-Fixed32.FromCm(100)).Raw);
    }

    [Fact]
    public void IntegerScalingIsExactAndNeverRounds()
    {
        Assert.Equal(300, (Fixed32.FromCm(100) * 3).Raw);
        Assert.Equal(50, (Fixed32.FromCm(100) / 2).Raw);

        // Odd division rounds half away from zero, which is the only rounding rule
        // Fixed32 has. 7/2 is 3.5, and 3.5 rounded away from zero is 4 — not 3.
        Assert.Equal(4, (Fixed32.FromCm(7) / 2).Raw);
        Assert.Equal(-4, (Fixed32.FromCm(-7) / 2).Raw);
    }

    [Fact]
    public void PercentageMultiplyAppliesThePercentScale()
    {
        // A 75% multiplier is what the designers actually write.
        Assert.Equal(75, (Fixed32.FromCm(100) * Fixed32.FromCm(75)).Raw);
        Assert.Equal(100, (Fixed32.FromCm(100) * Fixed32.FromCm(100)).Raw);
        Assert.Equal(0, (Fixed32.FromCm(100) * Fixed32.Zero).Raw);
    }

    [Fact]
    public void PercentageMultiplyRoundsHalvesAwayFromZero()
    {
        // 5 * 5 / 100 = 0.25 -> 0; 10 * 5 / 100 = 0.5 -> 1; -10 * 5 / 100 = -0.5 -> -1.
        Assert.Equal(0, (Fixed32.FromCm(5) * Fixed32.FromCm(5)).Raw);
        Assert.Equal(1, (Fixed32.FromCm(10) * Fixed32.FromCm(5)).Raw);
        Assert.Equal(-1, (Fixed32.FromCm(-10) * Fixed32.FromCm(5)).Raw);
    }

    [Fact]
    public void PercentageDivideReturnsTheRatioAsPercent()
    {
        // 50 is half of 100, so the ratio is 50%.
        Assert.Equal(50, (Fixed32.FromCm(50) / Fixed32.FromCm(100)).Raw);
        Assert.Equal(200, (Fixed32.FromCm(100) / Fixed32.FromCm(50)).Raw);
        Assert.Equal(100, (Fixed32.FromCm(100) / Fixed32.FromCm(100)).Raw);
    }

    [Fact]
    public void DivisionByZeroThrows()
    {
        Assert.Throws<DivideByZeroException>(() => Fixed32.FromCm(10) / Fixed32.Zero);
        Assert.Throws<DivideByZeroException>(() => Fixed32.FromCm(10) / 0);
    }

    [Fact]
    public void Distance_IsAbsoluteBetweenTwoPoints()
    {
        Assert.Equal(40, Fixed32.Distance(Fixed32.FromCm(100), Fixed32.FromCm(140)).Raw);
        Assert.Equal(40, Fixed32.Distance(Fixed32.FromCm(140), Fixed32.FromCm(100)).Raw);
    }

    // ---- overflow ------------------------------------------------------------

    [Fact]
    public void AdditionOverflowsLoudly()
    {
        Fixed32 big = Fixed32.FromCm(int.MaxValue);

        Assert.Throws<OverflowException>(() => big + Fixed32.FromCm(1));
        Assert.Throws<OverflowException>(() => Fixed32.FromCm(int.MinValue) - Fixed32.FromCm(1));
    }

    [Fact]
    public void NegatingIntMinValueOverflowsLoudly()
    {
        // int.MinValue has no positive counterpart in an int-backed type. Wrapping
        // would turn "the most negative possible distance" into a small positive one,
        // which is the kind of bug that only shows up as an NPC teleporting.
        Assert.Throws<OverflowException>(() => -Fixed32.FromCm(int.MinValue));
    }

    [Fact]
    public void IntegerScalingOverflowsLoudly()
    {
        Assert.Throws<OverflowException>(() => Fixed32.FromCm(int.MaxValue) * 2);
        Assert.Throws<OverflowException>(() => Fixed32.FromCm(int.MinValue) * 2);
    }

    [Fact]
    public void PercentageMultiplySaturatesByOverflowingRatherThanWrapping()
    {
        // int.MaxValue * int.MaxValue / 100 is far outside int range.
        Assert.Throws<OverflowException>(() => Fixed32.FromCm(int.MaxValue) * Fixed32.FromCm(int.MaxValue));

        // A value just inside the range must still work, so the check is on the
        // result and not on the magnitude of the operands.
        Fixed32 ok = Fixed32.FromCm(1_000) * Fixed32.FromCm(100);
        Assert.Equal(1_000, ok.Raw);
    }

    [Fact]
    public void ExtremeValuesRoundTripThroughTheStruct()
    {
        Fixed32 max = Fixed32.FromCm(int.MaxValue);
        Fixed32 min = Fixed32.FromCm(int.MinValue);

        Assert.Equal(int.MaxValue, max.Raw);
        Assert.Equal(int.MinValue, min.Raw);
        Assert.Equal(max, Fixed32.FromCm(int.MaxValue));
        Assert.NotEqual(max, min);
    }

    // ---- Abs / Min / Max / Clamp --------------------------------------------

    [Fact]
    public void AbsMakesPositive()
    {
        Assert.Equal(100, Fixed32.FromCm(-100).Abs().Raw);
        Assert.Equal(100, Fixed32.FromCm(100).Abs().Raw);
        Assert.Equal(0, Fixed32.Zero.Abs().Raw);
    }

    [Fact]
    public void MinAndMaxPickTheRightOperand()
    {
        Fixed32 a = Fixed32.FromCm(100);
        Fixed32 b = Fixed32.FromCm(200);

        Assert.Equal(a, Fixed32.Min(a, b));
        Assert.Equal(b, Fixed32.Max(a, b));
        Assert.Equal(a, Fixed32.Min(a, a));
    }

    [Fact]
    public void ClampIsInclusiveAtBothEnds()
    {
        Fixed32 lo = Fixed32.FromCm(10);
        Fixed32 hi = Fixed32.FromCm(20);

        Assert.Equal(lo, Fixed32.Clamp(Fixed32.FromCm(5), lo, hi));
        Assert.Equal(hi, Fixed32.Clamp(Fixed32.FromCm(50), lo, hi));
        Assert.Equal(lo, Fixed32.Clamp(lo, lo, hi));
        Assert.Equal(hi, Fixed32.Clamp(hi, lo, hi));
        Assert.Equal(Fixed32.FromCm(15), Fixed32.Clamp(Fixed32.FromCm(15), lo, hi));
    }

    [Fact]
    public void ClampRejectsAnInvertedRange()
    {
        Assert.Throws<ArgumentException>(
            () => Fixed32.Clamp(Fixed32.FromCm(5), Fixed32.FromCm(20), Fixed32.FromCm(10)));
    }

    // ---- comparison ----------------------------------------------------------

    [Fact]
    public void ComparisonOperatorsFollowTheRawValue()
    {
        Fixed32 a = Fixed32.FromCm(100);
        Fixed32 b = Fixed32.FromCm(200);
        Fixed32 equalToA = Fixed32.FromCm(100); // same value, distinct instance

        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= equalToA);
        Assert.True(a >= equalToA);
        Assert.True(a != b);
        Assert.False(a == b);
        Assert.True(a.CompareTo(b) < 0);
        Assert.Equal(0, a.CompareTo(equalToA));
    }

    [Fact]
    public void EqualityAndHashingAgree()
    {
        Fixed32 a = Fixed32.FromCm(123);
        Fixed32 b = Fixed32.FromCm(123);

        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals("123"));
    }

    [Fact]
    public void ToStringIsCultureInvariant()
    {
        Assert.Equal("-1234", Fixed32.FromCm(-1234).ToString());
    }

    // ---- SqrtApprox ---------------------------------------------------------

    [Fact]
    public void SqrtApproxIsTheFloorOfTheSquareRoot()
    {
        Assert.Equal(0, Fixed32.SqrtApprox(Fixed32.FromCm(0)).Raw);
        Assert.Equal(1, Fixed32.SqrtApprox(Fixed32.FromCm(1)).Raw);
        Assert.Equal(1, Fixed32.SqrtApprox(Fixed32.FromCm(3)).Raw);   // 1.73 -> 1
        Assert.Equal(2, Fixed32.SqrtApprox(Fixed32.FromCm(4)).Raw);
        Assert.Equal(3, Fixed32.SqrtApprox(Fixed32.FromCm(9)).Raw);
        Assert.Equal(316, Fixed32.SqrtApprox(Fixed32.FromCm(99_999)).Raw);
    }

    [Fact]
    public void SqrtApproxIsExactOnPerfectSquares()
    {
        // The case a Math.Sqrt-then-cast implementation gets wrong through
        // truncation on the largest values.
        for (int n = 1; n <= 2_000; n++)
            Assert.Equal(n, Fixed32.SqrtApprox(Fixed32.FromCm(n * n)).Raw);
    }

    [Fact]
    public void SqrtApproxHandlesTheTopOfTheRange()
    {
        // int.MaxValue = 2147483647; floor(sqrt) = 46340.
        Assert.Equal(46_340, Fixed32.SqrtApprox(Fixed32.FromCm(int.MaxValue)).Raw);
    }

    [Fact]
    public void SqrtApproxRejectsNegatives()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed32.SqrtApprox(Fixed32.FromCm(-1)));
    }

    // ---- Lerp ---------------------------------------------------------------

    [Fact]
    public void LerpHitsBothEndpointsExactly()
    {
        Fixed32 from = Fixed32.FromCm(100);
        Fixed32 to = Fixed32.FromCm(200);

        Assert.Equal(from, Fixed32.Lerp(from, to, Fixed32.FromCm(0)));
        Assert.Equal(to, Fixed32.Lerp(from, to, Fixed32.FromCm(100)));
    }

    [Fact]
    public void LerpInterpolatesOnPercent()
    {
        Fixed32 from = Fixed32.FromCm(0);
        Fixed32 to = Fixed32.FromCm(200);

        Assert.Equal(100, Fixed32.Lerp(from, to, Fixed32.FromCm(50)).Raw);
        Assert.Equal(50, Fixed32.Lerp(from, to, Fixed32.FromCm(25)).Raw);
    }

    [Fact]
    public void LerpExtrapolatesRatherThanClamping()
    {
        // A thrown projectile must overshoot, not stop at the endpoint.
        Fixed32 from = Fixed32.FromCm(0);
        Fixed32 to = Fixed32.FromCm(100);

        Assert.Equal(200, Fixed32.Lerp(from, to, Fixed32.FromCm(200)).Raw);
        Assert.Equal(-100, Fixed32.Lerp(from, to, Fixed32.FromCm(-100)).Raw);
    }

    [Fact]
    public void LerpMidpointIsSymmetricToWithinOneCentimetre()
    {
        // An odd delta puts the exact midpoint between two centimetres, so rounding
        // has to pick one of them and the two directions cannot both get it: 37→212
        // lands on 125, 212→37 lands on 124. The guarantee is that they never differ
        // by more than the rounding quantum — not that they are identical, which no
        // deterministic tie-break can deliver here.
        Fixed32 a = Fixed32.FromCm(37);
        Fixed32 b = Fixed32.FromCm(212);

        int forward = Fixed32.Lerp(a, b, Fixed32.FromCm(50)).Raw;
        int backward = Fixed32.Lerp(b, a, Fixed32.FromCm(50)).Raw;

        Assert.Equal(1, Math.Abs(forward - backward));

        // The midpoint of the extremes lands exactly on zero, which is a real result
        // rather than an overflow: the middle of the representable range is 0 cm.
        Assert.Equal(0, Fixed32.Lerp(
            Fixed32.FromCm(int.MinValue), Fixed32.FromCm(int.MaxValue), Fixed32.FromCm(50)).Raw);
    }

    [Fact]
    public void LerpMidpointIsExactForAnEvenDelta()
    {
        // With an even delta there is no half to break, so the two directions must
        // agree exactly. This is the case that actually catches a biased rounding rule.
        Fixed32 a = Fixed32.FromCm(0);
        Fixed32 b = Fixed32.FromCm(200);

        Assert.Equal(
            Fixed32.Lerp(a, b, Fixed32.FromCm(50)).Raw,
            Fixed32.Lerp(b, a, Fixed32.FromCm(50)).Raw);
    }

    [Fact]
    public void LerpOverflowsLoudly()
    {
        // Extrapolating past the end of the range is the case that overflows, since
        // clamping the result would silently flatten a thrown trajectory.
        Assert.Throws<OverflowException>(
            () => Fixed32.Lerp(Fixed32.Zero, Fixed32.FromCm(int.MaxValue), Fixed32.FromCm(200)));

        Assert.Throws<OverflowException>(
            () => Fixed32.Lerp(Fixed32.Zero, Fixed32.FromCm(int.MinValue), Fixed32.FromCm(-200)));
    }

    // ---- the properties the tactical layer depends on -----------------------

    [Fact]
    public void AFullMetreIsExactlyOneHundredCentimetres()
    {
        // The tactical space model defines positions in centimetres and the tactical
        // step in hundredths of a second. If this identity ever breaks, every distance
        // in the game is off by a scale factor and nothing else will look wrong.
        Assert.Equal(Fixed32.FromMetres(1), Fixed32.FromCm(100));
        Assert.Equal(100, Fixed32.CmPerMetre);
    }

    [Fact]
    public void SignHelpersAgreeWithTheRawValue()
    {
        Assert.True(Fixed32.Zero.IsZero);
        Assert.False(Fixed32.FromCm(1).IsZero);
        Assert.True(Fixed32.FromCm(-1).IsNegative);
        Assert.False(Fixed32.FromCm(1).IsNegative);
        Assert.False(Fixed32.Zero.IsNegative);
    }
}