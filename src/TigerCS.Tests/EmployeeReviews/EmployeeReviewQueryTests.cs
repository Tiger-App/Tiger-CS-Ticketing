using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.EmployeeReviews;

/// <summary>The dashboard over real data: totals, distribution, scope per role, UAE-calendar date filtering and the drill-down.</summary>
public sealed class EmployeeReviewQueryTests : IDisposable
{
    private static readonly EmployeeReviewFilterRequestDto NoFilter = new(null, null, null, null, null);
    private static readonly DateTime Today = EmployeeReviewFixture.Now;

    private readonly EmployeeReviewFixture _fx = new();
    private string _amalToken = "";
    private string _bilalToken = "";

    public void Dispose() => _fx.Dispose();

    /// <summary>Amal: 5, 4 (comment), 1 · Bilal: 3 · Hadi's tablet is registered directly (Collections is not Customer Service).</summary>
    private async Task SeedReviewsAsync()
    {
        _amalToken = (await _fx.RegisterAsync("Desk A", _fx.Amal)).Token;
        _bilalToken = (await _fx.RegisterAsync("Desk B", _fx.Bilal)).Token;
        await _fx.SubmitAsync(_amalToken, 5, null, Today.AddHours(-1));
        await _fx.SubmitAsync(_amalToken, 4, "Quick and kind", Today.AddHours(-2));
        await _fx.SubmitAsync(_amalToken, 1, "Long wait", Today.AddDays(-3));
        await _fx.SubmitAsync(_bilalToken, 3, "Okay", Today.AddHours(-3));

        // A review for an employee outside Customer Service (a tablet assigned before they moved): inserted directly.
        using var context = _fx.CreateContext();
        var device = new TigerCS.Domain.Modules.EmployeeReviews.EmployeeReviewDevice("Desk H", _fx.Hadi, new byte[32], Today.AddDays(-9));
        context.EmployeeReviewDevices.Add(device);
        context.EmployeeReviews.Add(new TigerCS.Domain.Modules.EmployeeReviews.EmployeeReview(device, Guid.NewGuid(), 2, "Collections feedback", Today.AddHours(-4)));
        context.SaveChanges();
    }

    private async Task<EmployeeReviewsOverviewDto> Overview(Guid caller, string role, EmployeeReviewFilterRequestDto? filter = null)
    {
        using var context = _fx.CreateContext();
        return await _fx.CreateQueryService(context).GetOverviewAsync(caller, [role], filter ?? NoFilter);
    }

    [Fact]
    public async Task Manager_SeesEveryone_WithTotalsAverageAndTheFiveStarDistribution()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Manager, Roles.CsManager);

        Assert.Equal(5, overview.Summary.TotalReviews);
        Assert.Equal(3.0, overview.Summary.AverageRating);   // (5+4+1+3+2)/5
        Assert.Equal([5, 4, 3, 2, 1], overview.Summary.Distribution.Select(s => s.Rating));
        Assert.Equal([1, 1, 1, 1, 1], overview.Summary.Distribution.Select(s => s.Count));
        Assert.Equal(3, overview.Employees.Count);
        var amal = overview.Employees.Single(e => e.EmployeeName == "Amal Agent");
        Assert.Equal(3, amal.ReviewCount);
        Assert.Equal(3.33, amal.AverageRating);
        Assert.Equal("Customer Service", amal.Department);
        Assert.Equal("Amal Agent", overview.Employees[0].EmployeeName); // most reviews first
    }

    [Fact]
    public async Task Comments_ListOnlyReviewsThatHaveOne_NewestFirst_WithEmployeeDepartmentAndUaeTime()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Admin, Roles.SystemAdministrator);

        // Newest first: Quick (-2h), Okay (-3h), Collections (-4h), Long wait (-3 days).
        Assert.Equal(["Quick and kind", "Okay", "Collections feedback", "Long wait"], overview.Comments.Select(c => c.Comment));
        var first = overview.Comments[2];
        Assert.Equal("Hadi Head", first.EmployeeName);
        Assert.Equal("Collections", first.Department);
        Assert.Equal(first.CreatedAtUtc.AddHours(4), first.CreatedAtUae);
    }

    [Fact]
    public async Task Employee_SeesOnlyTheirOwnReviews()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Amal, Roles.CsAgent);

        Assert.Equal(3, overview.Summary.TotalReviews);
        Assert.Equal("Amal Agent", Assert.Single(overview.Employees).EmployeeName);
        Assert.DoesNotContain(overview.Comments, c => c.EmployeeName != "Amal Agent");
        Assert.Equal([_fx.Amal], overview.Filters.Employees.Select(e => e.EmployeeId));
    }

    [Fact]
    public async Task Employee_CannotWidenTheirScopeByAskingForSomeoneElse()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Amal, Roles.CsAgent, NoFilter with { EmployeeId = _fx.Bilal });
        Assert.Equal(0, overview.Summary.TotalReviews);

        using var context = _fx.CreateContext();
        var drill = await _fx.CreateQueryService(context).GetEmployeeAsync(_fx.Amal, [Roles.CsAgent], _fx.Bilal, NoFilter);
        Assert.Equal(EmployeeReviewQueryOutcome.Forbidden, drill.Outcome);
    }

    [Fact]
    public async Task Supervisor_SeesTheEmployeesOfTheirOwnDepartments_NotOtherDepartments()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Supervisor, Roles.CsSupervisor);

        Assert.Equal(["Amal Agent", "Bilal Agent"], overview.Employees.Select(e => e.EmployeeName).Order());
        Assert.Equal(4, overview.Summary.TotalReviews);
        Assert.Equal(["Customer Service"], overview.Filters.Departments.Select(d => d.Name));

        using var context = _fx.CreateContext();
        var service = _fx.CreateQueryService(context);
        Assert.Equal(EmployeeReviewQueryOutcome.Success, (await service.GetEmployeeAsync(_fx.Supervisor, [Roles.CsSupervisor], _fx.Amal, NoFilter)).Outcome);
        Assert.Equal(EmployeeReviewQueryOutcome.Forbidden, (await service.GetEmployeeAsync(_fx.Supervisor, [Roles.CsSupervisor], _fx.Hadi, NoFilter)).Outcome);
    }

    [Fact]
    public async Task Supervisor_AskingForAnotherDepartment_GetsNothing_NotSomeoneElsesNumbers()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Supervisor, Roles.CsSupervisor, NoFilter with { DepartmentId = _fx.CollectionsId });

        Assert.Equal(0, overview.Summary.TotalReviews);
        Assert.Empty(overview.Employees);
    }

    [Theory]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task ManagementRoles_SeeEveryEmployee(string role)
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Manager, role);

        Assert.Equal(5, overview.Summary.TotalReviews);
    }

    [Fact]
    public async Task Filters_NarrowByEmployee_Department_AndStarRating()
    {
        await SeedReviewsAsync();

        Assert.Equal(1, (await Overview(_fx.Manager, Roles.CsManager, NoFilter with { EmployeeId = _fx.Bilal })).Summary.TotalReviews);
        Assert.Equal(4, (await Overview(_fx.Manager, Roles.CsManager, NoFilter with { DepartmentId = _fx.CustomerServiceId })).Summary.TotalReviews);
        Assert.Equal(1, (await Overview(_fx.Manager, Roles.CsManager, NoFilter with { DepartmentId = _fx.CollectionsId })).Summary.TotalReviews);

        var fiveStar = await Overview(_fx.Manager, Roles.CsManager, NoFilter with { Rating = 5 });
        Assert.Equal(1, fiveStar.Summary.TotalReviews);
        Assert.Equal(5.0, fiveStar.Summary.AverageRating);

        var combined = await Overview(_fx.Manager, Roles.CsManager, NoFilter with { EmployeeId = _fx.Amal, Rating = 1 });
        Assert.Equal(1, combined.Summary.TotalReviews);
    }

    [Fact]
    public async Task OutOfRangeStarFilter_IsIgnored_RatherThanFailing()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Manager, Roles.CsManager, NoFilter with { Rating = 9 });

        Assert.Null(overview.Filters.Rating);
        Assert.Equal(5, overview.Summary.TotalReviews);
    }

    [Fact]
    public async Task DefaultPeriod_IsTheLast30UaeDays_EndingToday()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Manager, Roles.CsManager);

        Assert.Equal(new DateOnly(2026, 9, 10), overview.Filters.DateTo);
        Assert.Equal(new DateOnly(2026, 8, 12), overview.Filters.DateFrom);
    }

    [Fact]
    public async Task DateFilter_UsesUaeCalendarDays_NotUtcDays()
    {
        var (_, token) = await _fx.RegisterAsync("Desk A", _fx.Amal);
        // 9 Sep 20:30 UTC is 10 Sep 00:30 in the UAE; 10 Sep 19:59 UTC is 10 Sep 23:59 UAE; 10 Sep 20:00 UTC is 11 Sep 00:00 UAE.
        await _fx.SubmitAsync(token, 5, "just after UAE midnight", new DateTime(2026, 9, 9, 20, 30, 0, DateTimeKind.Utc));
        await _fx.SubmitAsync(token, 4, "last minute of the UAE day", new DateTime(2026, 9, 10, 19, 59, 0, DateTimeKind.Utc));
        await _fx.SubmitAsync(token, 3, "next UAE day", new DateTime(2026, 9, 10, 20, 0, 0, DateTimeKind.Utc));
        await _fx.SubmitAsync(token, 2, "previous UAE day", new DateTime(2026, 9, 9, 19, 59, 0, DateTimeKind.Utc));

        var sep10 = await Overview(_fx.Manager, Roles.CsManager, new(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 10), null, null, null));
        Assert.Equal(["last minute of the UAE day", "just after UAE midnight"], sep10.Comments.Select(c => c.Comment));
        Assert.Equal(2, sep10.Summary.TotalReviews);

        var sep11 = await Overview(_fx.Manager, Roles.CsManager, new(new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 11), null, null, null));
        Assert.Equal("next UAE day", Assert.Single(sep11.Comments).Comment);

        var sep9 = await Overview(_fx.Manager, Roles.CsManager, new(new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 9), null, null, null));
        Assert.Equal("previous UAE day", Assert.Single(sep9.Comments).Comment);
    }

    [Fact]
    public async Task ReportedTimes_AreUaeTime_WhileStoredTimesStayUtc()
    {
        var (_, token) = await _fx.RegisterAsync("Desk A", _fx.Amal);
        var utc = new DateTime(2026, 9, 9, 20, 30, 0, DateTimeKind.Utc);
        await _fx.SubmitAsync(token, 5, "x", utc);

        var entry = Assert.Single((await Overview(_fx.Manager, Roles.CsManager, new(new DateOnly(2026, 9, 1), null, null, null, null))).Comments);

        Assert.Equal(utc, entry.CreatedAtUtc);
        Assert.Equal(new DateTime(2026, 9, 10, 0, 30, 0), entry.CreatedAtUae);
    }

    [Fact]
    public async Task BackwardsDates_AreSwapped()
    {
        await SeedReviewsAsync();

        var overview = await Overview(_fx.Manager, Roles.CsManager, new(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 1), null, null, null));

        Assert.Equal(new DateOnly(2026, 9, 1), overview.Filters.DateFrom);
        Assert.Equal(5, overview.Summary.TotalReviews);
    }

    [Fact]
    public async Task EmptyPeriod_ReturnsZeroesAndNoAverage()
    {
        var overview = await Overview(_fx.Manager, Roles.CsManager);

        Assert.Equal(0, overview.Summary.TotalReviews);
        Assert.Null(overview.Summary.AverageRating);
        Assert.Equal(5, overview.Summary.Distribution.Count);
        Assert.Empty(overview.Employees);
    }

    [Fact]
    public async Task DrillDown_ShowsOneEmployeesHistoryAndBreakdown_EvenWhenTheTabletMovedOn()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk A", _fx.Amal);
        await _fx.SubmitAsync(token, 5, "great", Today.AddHours(-5));
        await _fx.SubmitAsync(token, 2, null, Today.AddHours(-4));
        using (var context = _fx.CreateContext())
        {
            await _fx.CreateAdminService(context).UpdateAsync(_fx.Admin, deviceId, new("Desk A", _fx.Bilal));
        }

        await _fx.SubmitAsync(token, 1, "for Bilal", Today.AddHours(-1));

        using var check = _fx.CreateContext();
        var amal = (await _fx.CreateQueryService(check).GetEmployeeAsync(_fx.Manager, [Roles.CsManager], _fx.Amal, NoFilter)).Value!;
        Assert.Equal("Amal Agent", amal.EmployeeName);
        Assert.Equal(2, amal.Summary.TotalReviews);
        Assert.Equal(3.5, amal.Summary.AverageRating);
        Assert.Equal(["great", null], amal.Reviews.Select(r => r.Comment).OrderByDescending(c => c));

        var bilal = (await _fx.CreateQueryService(check).GetEmployeeAsync(_fx.Manager, [Roles.CsManager], _fx.Bilal, NoFilter)).Value!;
        Assert.Equal("for Bilal", Assert.Single(bilal.Reviews).Comment);
    }

    [Fact]
    public async Task DrillDown_IgnoresAnEmployeeFilterCarriedFromTheOverview()
    {
        await SeedReviewsAsync();
        using var context = _fx.CreateContext();

        var detail = (await _fx.CreateQueryService(context).GetEmployeeAsync(_fx.Manager, [Roles.CsManager], _fx.Amal, NoFilter with { EmployeeId = _fx.Bilal })).Value!;

        Assert.Equal(3, detail.Summary.TotalReviews);
    }
}
