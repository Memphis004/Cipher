using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>Proves tick/day/week arithmetic, including every rollover boundary.</summary>
public class TickTests
{
    [Fact]
    public void ZeroTick_IsDayZeroHourZeroWeekZero()
    {
        Tick t = Tick.Zero;
        Assert.Equal(0, t.Day);
        Assert.Equal(0, t.HourOfDay);
        Assert.Equal(0, t.Week);
        Assert.Equal(0, t.DayOfWeek);
    }

    [Fact]
    public void FromDays_MapsDaysToMidnightTicks()
    {
        Assert.Equal(0, Tick.FromDays(0).Value);
        Assert.Equal(24, Tick.FromDays(1).Value);
        Assert.Equal(24 * 30, Tick.FromDays(30).Value);
        Assert.Equal(24 * 365, Tick.FromDays(365).Value);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]       // midnight of day 0
    [InlineData(24, 1, 0, 0)]       // midnight: day rolls
    [InlineData(167, 6, 0, 23)]     // last tick of week 0
    [InlineData(168, 7, 1, 0)]      // midnight of day 7: both day and week roll
    [InlineData(1440, 60, 8, 0)]    // day 60 -> week 8
    public void Components_AreConsistentWithValue(long value, int day, int week, int hour)
    {
        Tick t = new(value);
        Assert.Equal(day, t.Day);
        Assert.Equal(week, t.Week);
        Assert.Equal(hour, t.HourOfDay);
    }

    [Fact]
    public void DayOfWeek_WrapsEverySevenDays()
    {
        for (int day = 0; day < 21; day++)
            Assert.Equal(day % 7, Tick.FromDays(day).DayOfWeek);
    }

    [Fact]
    public void HourOfDay_StepsThroughTheDay()
    {
        for (int hour = 0; hour < Tick.TicksPerDay; hour++)
            Assert.Equal(hour, Tick.FromDayHour(2, hour).HourOfDay);
    }

    [Fact]
    public void ArithmeticOperators_BehaveLikeIntegers()
    {
        Tick a = new(100);
        Tick b = new(23);

        Assert.Equal(123, (a + b).Value);
        Assert.Equal(77, (a - b).Value);
        Assert.Equal(123, (a + 23).Value);
        Assert.Equal(77, (a - 23).Value);
        Assert.Equal(-100, (-a).Value);
        Assert.Equal(101, (a + 1).Value);
        Assert.Equal(99, (a - 1).Value);
    }

    [Fact]
    public void ComparisonOperators_CompareUnderlyingValue()
    {
        Assert.True(new Tick(5) < new Tick(6));
        Assert.True(new Tick(6) > new Tick(5));
        Assert.True(new Tick(5) <= new Tick(5));
        Assert.True(new Tick(5) >= new Tick(5));
        Assert.True(new Tick(5) == new Tick(5));
        Assert.True(new Tick(5) != new Tick(6));
        Assert.Equal(0, new Tick(5).CompareTo(new Tick(5)));
        Assert.True(new Tick(4).CompareTo(new Tick(5)) < 0);
    }

    [Fact]
    public void StartOfDay_And_StartOfWeek_SnapToBoundaries()
    {
        Tick t = Tick.FromDayHour(10, 13);

        // StartOfDay is the same day's midnight; StartOfWeek is the Monday of that week.
        // Day 10 falls in week 1, which began on day 7.
        Assert.Equal(Tick.FromDays(10), t.StartOfDay());
        Assert.Equal(new Tick(7 * Tick.TicksPerDay), t.StartOfWeek());
    }

    [Fact]
    public void IsSameDay_And_IsSameWeek_GroupCorrectly()
    {
        Tick monday = Tick.FromDayHour(7, 0);
        Tick mondayLate = Tick.FromDayHour(7, 23);
        Tick tuesday = Tick.FromDayHour(8, 0);

        Assert.True(monday.IsSameDay(mondayLate));
        Assert.False(monday.IsSameDay(tuesday));
        Assert.True(monday.IsSameWeek(mondayLate));
        Assert.True(monday.IsSameWeek(tuesday)); // same week
    }

    [Fact]
    public void DaysUntil_IsSigned()
    {
        Assert.Equal(3, Tick.FromDays(4).DaysUntil(Tick.FromDays(7)));
        Assert.Equal(-3, Tick.FromDays(7).DaysUntil(Tick.FromDays(4)));
    }

    // ---- GameClock -----------------------------------------------------------

    [Fact]
    public void Clock_Advance_MovesOneTickAtATime()
    {
        var clock = new GameClock();

        clock.Advance();

        Assert.Equal(1, clock.Current.Value);
        Assert.Equal(1, clock.TicksElapsed);
    }

    [Fact]
    public void Clock_RaisesTickDayAndWeekEvents_AtTheRightTicks()
    {
        var clock = new GameClock();
        var ticks = new List<long>();
        var days = new List<int>();
        var weeks = new List<int>();

        using IDisposable _a = clock.OnTick(e => ticks.Add(e.Tick.Value));
        using IDisposable _b = clock.OnDayChanged(e => days.Add(e.Day));
        using IDisposable _c = clock.OnWeekChanged(e => weeks.Add(e.NewWeek));

        // 24 ticks: cross one midnight, no week boundary.
        clock.Advance(Tick.TicksPerDay);

        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), ticks);
        Assert.Equal(new[] { 1 }, days);
        Assert.Empty(weeks);
    }

    [Fact]
    public void Clock_WeekBoundary_RaisesDayThenWeek()
    {
        var clock = new GameClock();
        var order = new List<string>();

        using IDisposable _a = clock.OnDayChanged(_ => order.Add("day"));
        using IDisposable _b = clock.OnWeekChanged(_ => order.Add("week"));

        // Advance to the last tick of week 0, discarding the seven ordinary
        // midnights that cross along the way.
        clock.Advance(Tick.TicksPerWeek - 1);
        order.Clear();

        clock.Advance(1); // tick 168: midnight of day 7, first tick of week 1

        // Day must be reported before week: week contains days, so the reverse order
        // would let a week handler run against a world whose day state is stale.
        Assert.Equal(new[] { "day", "week" }, order);
    }

    [Fact]
    public void Clock_RaisesExactlyOneDayEventPerMidnight()
    {
        var clock = new GameClock();
        int dayEvents = 0;
        using IDisposable _ = clock.OnDayChanged(_ => dayEvents++);

        clock.Advance(Tick.TicksPerDay * 30);

        Assert.Equal(30, dayEvents);
    }

    [Fact]
    public void Clock_Advance_RejectsNegativeSteps()
    {
        var clock = new GameClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-1));
    }

    [Fact]
    public void Clock_SetTo_MovesForwardAndRaisesEvents()
    {
        var clock = new GameClock();
        int days = 0;
        using IDisposable _ = clock.OnDayChanged(_ => days++);

        clock.SetTo(Tick.FromDays(3));

        Assert.Equal(Tick.FromDays(3), clock.Current);
        Assert.Equal(3, days);
    }

    [Fact]
    public void Clock_Unsubscribe_StopsDelivery()
    {
        var clock = new GameClock();
        int count = 0;

        IDisposable subscription = clock.OnTick(_ => count++);
        clock.Advance();
        Assert.Equal(1, count);

        subscription.Dispose();
        clock.Advance();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Clock_OverALongRun_CountsMatchArithmetic()
    {
        var clock = new GameClock();
        const int totalTicks = Tick.TicksPerDay * 365;

        clock.Advance(totalTicks);

        Assert.Equal(365, clock.CurrentDay);
        Assert.Equal(totalTicks / Tick.TicksPerWeek, clock.CurrentWeek);
    }
}
