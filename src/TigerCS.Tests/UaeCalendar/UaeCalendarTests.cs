using TigerCS.Domain.Common;

namespace TigerCS.Tests.UaeCalendarSuite;

/// <summary>
/// The UAE calendar helper at the UAE midnight boundary (00:00 UAE = 20:00Z the evening before), at month ends and on a leap day. These run on a
/// UTC host and under <c>TZ=Asia/Dubai</c> (CI runs both): the result must never depend on the host's zone.
/// </summary>
public sealed class UaeCalendarTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) => new(y, m, d, h, min, s, DateTimeKind.Utc);

    [Fact]
    public void ToUaeDate_OneSecondBeforeUaeMidnight_IsStillThePreviousUaeDay()
    {
        Assert.Equal(new DateOnly(2026, 11, 19), UaeCalendar.ToUaeDate(Utc(2026, 11, 19, 19, 59, 59)));
    }

    [Fact]
    public void ToUaeDate_AtUaeMidnight_IsTheNewUaeDay()
    {
        Assert.Equal(new DateOnly(2026, 11, 20), UaeCalendar.ToUaeDate(Utc(2026, 11, 19, 20, 0, 0)));
    }

    [Theory]
    [InlineData(19, 30, 20)]   // 23:30 UAE on the 20th is 19:30Z on the 20th
    [InlineData(20, 30, 21)]   // 00:30 UAE on the 21st is 20:30Z on the 20th
    [InlineData(23, 59, 21)]   // 03:59 UAE on the 21st is 23:59Z on the 20th
    public void ToUaeDate_BucketsTheUaeEveningAndTheSmallHoursOnTheirOwnUaeDays(int utcHour, int utcMinute, int expectedUaeDay)
    {
        Assert.Equal(new DateOnly(2026, 11, expectedUaeDay), UaeCalendar.ToUaeDate(Utc(2026, 11, 20, utcHour, utcMinute)));
    }

    [Fact]
    public void DayStartUtc_IsTwentyHundredHoursUtcTheEveningBefore()
    {
        var start = UaeCalendar.DayStartUtc(new DateOnly(2026, 11, 20));

        Assert.Equal(Utc(2026, 11, 19, 20), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Fact]
    public void DayRangeUtc_IsHalfOpen_AndAnInclusiveUaeToDateCoversThatWholeDay()
    {
        var (start, end) = UaeCalendar.DayRangeUtc(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 22));

        Assert.Equal(Utc(2026, 11, 19, 20), start);
        Assert.Equal(Utc(2026, 11, 22, 20), end);                                   // exclusive: 00:00 UAE on the 23rd
        Assert.True(Utc(2026, 11, 22, 19, 59, 59) < end);                          // 23:59:59 UAE on the 22nd is inside
        Assert.False(Utc(2026, 11, 22, 20) < end);                                 // 00:00 UAE on the 23rd is outside
    }

    [Fact]
    public void DayRangeUtc_SingleDay_Is24Hours()
    {
        var day = new DateOnly(2026, 1, 1);
        var (start, end) = UaeCalendar.DayRangeUtc(day, day);

        Assert.Equal(Utc(2025, 12, 31, 20), start);
        Assert.Equal(TimeSpan.FromHours(24), end - start);
    }

    [Fact]
    public void TodayRangeUtc_AtTwentyHundredUtc_RollsToTheNextUaeDay()
    {
        var (before, _) = UaeCalendar.TodayRangeUtc(Utc(2026, 11, 19, 19, 59, 59));
        var (after, afterEnd) = UaeCalendar.TodayRangeUtc(Utc(2026, 11, 19, 20, 0, 0));

        Assert.Equal(Utc(2026, 11, 18, 20), before);
        Assert.Equal(Utc(2026, 11, 19, 20), after);
        Assert.Equal(Utc(2026, 11, 20, 20), afterEnd);
    }

    [Theory]
    [InlineData(2026, 1, 31, 2026, 2)]
    [InlineData(2026, 2, 28, 2026, 3)]    // non-leap February ends on the 28th
    [InlineData(2028, 2, 29, 2028, 3)]    // leap February ends on the 29th
    [InlineData(2026, 12, 31, 2027, 1)]   // year end
    public void MonthRangeUtc_RunsFromTheFirstUaeMidnightToTheNextMonthsFirstUaeMidnight(int year, int month, int lastDay, int nextYear, int nextMonth)
    {
        var (start, end) = UaeCalendar.MonthRangeUtc(year, month);

        Assert.Equal(UaeCalendar.DayStartUtc(new DateOnly(year, month, 1)), start);
        Assert.Equal(UaeCalendar.DayStartUtc(new DateOnly(nextYear, nextMonth, 1)), end);
        // The last day of the month at 23:59:59 UAE is inside; the next month's first second is not.
        var lastSecond = UaeCalendar.DayEndUtc(new DateOnly(year, month, lastDay)).AddSeconds(-1);
        Assert.True(lastSecond >= start && lastSecond < end);
        Assert.False(UaeCalendar.DayEndUtc(new DateOnly(year, month, lastDay)) < end);
    }

    [Fact]
    public void MonthRangeUtc_LeapFebruary_Is29Days_AndNonLeapIs28()
    {
        var leap = UaeCalendar.MonthRangeUtc(2028, 2);
        var common = UaeCalendar.MonthRangeUtc(2026, 2);

        Assert.Equal(TimeSpan.FromDays(29), leap.EndUtc - leap.StartUtc);
        Assert.Equal(TimeSpan.FromDays(28), common.EndUtc - common.StartUtc);
        Assert.Equal(Utc(2028, 2, 28, 20), UaeCalendar.DayStartUtc(new DateOnly(2028, 2, 29)));   // the leap day starts on the 28th at 20:00Z
    }

    [Fact]
    public void ToUae_ConvertsByTheFixedPlusFourOffset_AndTreatsAnUnspecifiedValueAsUtc()
    {
        var unspecified = DateTime.SpecifyKind(new DateTime(2026, 11, 19, 20, 0, 0), DateTimeKind.Unspecified);

        Assert.Equal(new DateTime(2026, 11, 20, 0, 0, 0), UaeCalendar.ToUae(Utc(2026, 11, 19, 20)));
        Assert.Equal(new DateTime(2026, 11, 20, 0, 0, 0), UaeCalendar.ToUae(unspecified));
    }

    [Fact]
    public void Zone_IsAsiaDubai_NeverTheHostZone()
    {
        Assert.Equal(TimeSpan.FromHours(4), UaeCalendar.Zone.GetUtcOffset(Utc(2026, 7, 1)));
        Assert.Equal(TimeSpan.FromHours(4), UaeCalendar.Zone.GetUtcOffset(Utc(2026, 1, 1)));   // no daylight saving
        Assert.False(UaeCalendar.Zone.SupportsDaylightSavingTime);
    }

    [Fact]
    public void NullPropagatingBounds_ReturnNullForNoFilter_AndTheUaeBoundsOtherwise()
    {
        Assert.Null(UaeCalendar.DayStartUtcOrNull(null));
        Assert.Null(UaeCalendar.DayEndUtcOrNull(null));
        Assert.Equal(Utc(2026, 11, 19, 20), UaeCalendar.DayStartUtcOrNull(new DateOnly(2026, 11, 20)));
        Assert.Equal(Utc(2026, 11, 20, 20), UaeCalendar.DayEndUtcOrNull(new DateOnly(2026, 11, 20)));
    }
}
