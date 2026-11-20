using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Common;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// Every business-day filter on the ticket side uses UAE calendar boundaries over UTC storage. Tickets are seeded at 23:30 UAE, 00:30 UAE and
/// 03:59 UAE of one UAE day (the hours the old UTC-day bucketing put on the previous date) and at the two seconds that straddle the day, then read
/// back through the real services and real SQL. Run on a UTC host and under <c>TZ=Asia/Dubai</c>; both must agree.
/// </summary>
public sealed class UaeBusinessDayFilterTests : IDisposable
{
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];

    /// <summary>The UAE business day under test.</summary>
    private static readonly DateOnly Day = new(2026, 9, 10);

    // UAE wall-clock → the stored UTC instant. 00:30 UAE on the 10th is 20:30Z on the 9th.
    private static DateTime Uae(DateOnly day, int hour, int minute, int second = 0) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute, second)).AddHours(-4), DateTimeKind.Utc);

    private static readonly DateTime LastSecondOfPreviousDay = Uae(Day.AddDays(-1), 23, 59, 59);
    private static readonly DateTime Midnight = Uae(Day, 0, 0);
    private static readonly DateTime HalfPastMidnight = Uae(Day, 0, 30);
    private static readonly DateTime LastMinuteOfSmallHours = Uae(Day, 3, 59);
    private static readonly DateTime LateEvening = Uae(Day, 23, 30);
    private static readonly DateTime LastSecondOfDay = Uae(Day, 23, 59, 59);
    private static readonly DateTime NextMidnight = Uae(Day.AddDays(1), 0, 0);

    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private void SeedCreatedAround()
    {
        using var context = _db.CreateContext();
        foreach (var created in new[] { LastSecondOfPreviousDay, Midnight, HalfPastMidnight, LastMinuteOfSmallHours, LateEvening, NextMidnight })
        {
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: created);
        }
    }

    private async Task<DashboardOverviewDto> OverviewAsync(DateTime nowUtc, DashboardOverviewRequestDto? request = null)
    {
        using var context = _db.CreateContext();
        return await _db.CreateService(context, nowUtc).GetOverviewAsync(Guid.NewGuid(), CrossDepartment, request ?? new DashboardOverviewRequestDto());
    }

    [Fact]
    public async Task Dashboard_VolumeForAUaeDay_IncludesTheSmallHoursAndTheEvening_AndExcludesBothNeighbours()
    {
        SeedCreatedAround();

        var overview = await OverviewAsync(Uae(Day, 12, 0), new DashboardOverviewRequestDto(Day, Day));

        // 00:00, 00:30, 03:59 and 23:30 UAE belong to the 10th; 23:59:59 UAE on the 9th and 00:00 UAE on the 11th do not.
        Assert.Equal(4, overview.VolumeTotal);
    }

    [Fact]
    public async Task Dashboard_VolumeForTheUaePreviousDay_HoldsOnlyItsOwnLastSecond()
    {
        SeedCreatedAround();

        var overview = await OverviewAsync(Uae(Day, 12, 0), new DashboardOverviewRequestDto(Day.AddDays(-1), Day.AddDays(-1)));

        Assert.Equal(1, overview.VolumeTotal);
    }

    [Fact]
    public async Task Dashboard_DefaultPeriod_AtHalfPastMidnightUae_AlreadyEndsOnTheNewUaeDay_AndCountsAnythingCreatedSince()
    {
        SeedCreatedAround();

        // 20:30Z on the 9th is 00:30 UAE on the 10th: the old UTC logic still said "the 9th".
        var overview = await OverviewAsync(HalfPastMidnight);

        Assert.Equal(Day, overview.Filters.DateTo);
        Assert.Equal(Day.AddDays(-(TigerCS.Application.Modules.Ticketing.Services.DashboardAppService.DefaultPeriodDays - 1)), overview.Filters.DateFrom);
        Assert.Equal(5, overview.VolumeTotal);   // last second of the 9th, 00:00, 00:30, 03:59, 23:30 — the 11th's midnight is not yet in the period
    }

    [Fact]
    public async Task Dashboard_DueToday_FollowsTheUaeDay_NotTheUtcDay()
    {
        using (var context = _db.CreateContext())
        {
            foreach (var due in new[] { Midnight, HalfPastMidnight, LastMinuteOfSmallHours, LateEvening, LastSecondOfDay })
            {
                _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Uae(Day.AddDays(-3), 9, 0), resolutionDueAtUtc: due);
            }

            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Uae(Day.AddDays(-3), 9, 0), resolutionDueAtUtc: LastSecondOfPreviousDay);
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Uae(Day.AddDays(-3), 9, 0), resolutionDueAtUtc: NextMidnight);
        }

        // Evaluated at 00:30 UAE — the instant the old UTC day still called "yesterday".
        var overview = await OverviewAsync(HalfPastMidnight);

        Assert.Equal(5, overview.Kpis.DueToday);
    }

    [Fact]
    public async Task TicketQueue_CreatedRange_UsesUaeDayBoundaries()
    {
        SeedCreatedAround();

        using var context = _db.CreateContext();
        var result = await _db.CreateQueryService(context, Uae(Day, 12, 0)).GetQueueAsync(
            Guid.NewGuid(), CrossDepartment,
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100, CreatedFrom: Day, CreatedTo: Day));

        Assert.Equal(4, result.TotalCount);
    }

    [Fact]
    public async Task TicketQueue_DueTodayFilter_FollowsTheUaeDay()
    {
        using (var context = _db.CreateContext())
        {
            foreach (var due in new[] { LastSecondOfPreviousDay, Midnight, LastMinuteOfSmallHours, LateEvening, NextMidnight })
            {
                _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Uae(Day.AddDays(-3), 9, 0), resolutionDueAtUtc: due);
            }
        }

        using var queryContext = _db.CreateContext();
        var result = await _db.CreateQueryService(queryContext, HalfPastMidnight).GetQueueAsync(
            Guid.NewGuid(), CrossDepartment,
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100, DueToday: true));

        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task ClosureRange_UsesUaeDayBoundaries_OnTheDashboardAndTheQueueItLinksTo()
    {
        using (var context = _db.CreateContext())
        {
            foreach (var closedAt in new[] { LastSecondOfPreviousDay, Midnight, HalfPastMidnight, LastMinuteOfSmallHours, LateEvening, NextMidnight })
            {
                _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: closedAt.AddHours(-30), status: TicketStatus.Closed, closedAtUtc: closedAt);
            }
        }

        var overview = await OverviewAsync(Uae(Day, 12, 0), new DashboardOverviewRequestDto(Day, Day));
        Assert.Equal(4, overview.Kpis.ReviewedTickets + overview.Kpis.NotReviewedTickets);

        using var context2 = _db.CreateContext();
        var queue = await _db.CreateQueryService(context2, Uae(Day, 12, 0)).GetQueueAsync(
            Guid.NewGuid(), CrossDepartment,
            new TicketListRequestDto(null, null, null, nameof(TicketStatus.Closed), null, null, null, null, null, 1, 100, ClosedFrom: Day, ClosedTo: Day));
        Assert.Equal(4, queue.TotalCount);
    }

    [Fact]
    public void DashboardTodayStart_IsTheUtcInstantTheUaeDayBegan()
    {
        Assert.Equal(Midnight, UaeCalendar.TodayRangeUtc(HalfPastMidnight).StartUtc);
        Assert.Equal(Midnight, UaeCalendar.TodayRangeUtc(LastSecondOfDay).StartUtc);
        Assert.Equal(NextMidnight, UaeCalendar.TodayRangeUtc(NextMidnight).StartUtc);
        Assert.Equal(Midnight.AddHours(-24), UaeCalendar.TodayRangeUtc(LastSecondOfPreviousDay).StartUtc);
    }
}
