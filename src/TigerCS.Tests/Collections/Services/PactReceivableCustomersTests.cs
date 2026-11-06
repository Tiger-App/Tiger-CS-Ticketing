using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Services;

public sealed class PactReceivableCustomersTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc); // Oct 7 in Dubai
    private sealed class Source : IPactReceivablesSource
    {
        public List<PactReceivableInstalment> Items { get; } = [];
        public int Reads { get; private set; }
        public DateOnly? ThroughDate { get; private set; }
        public Exception? Failure { get; set; }
        public Func<CancellationToken, Task>? Delay { get; set; }
        public async Task<PactReceivablesSnapshot> ReadAsync(DateOnly businessDate, CancellationToken cancellationToken)
        {
            Reads++;
            ThroughDate = businessDate;
            if (Failure is not null) throw Failure;
            if (Delay is not null) await Delay(cancellationToken);
            return new PactReceivablesSnapshot(Items, Now, true);
        }
    }

    private sealed class Harness
    {
        public Source Source { get; } = new();
        public CollectionsOptions Options { get; } = new() { Enabled = true };
        public PactReceivablesOptions SqlOptions { get; } = new() { Enabled = true, DefaultMinOutstandingAmount = 0m };
        public PactReceivableCustomersAppService Service { get; }
        public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);
        public Harness()
        {
            Service = new(Options, SqlOptions,
                new CollectionsAuthorizationService(Options, new FakeDepartmentRepository()),
                new CollectionsClock(Options, new FakeTimeProvider(Now)), Source,
                NullLogger<PactReceivableCustomersAppService>.Instance);
        }
    }

    internal static PactReceivableInstalment Row(int day, decimal amount = 100m,
        string tenant = "3001", int company = 4, string unit = "TP140-101", string voucher = "INV-1", int month = 10, int year = 2026) =>
        new(company, tenant, "Example Customer", "+971 50 000 3001", "example@example.test", 101,
            unit, "", voucher, "", new DateTime(year, month, day), amount, "Installment");

    [Fact]
    public async Task MonthlyDefinition_DueIsTheWholeMonthIncludingLaterDays_OverdueIsBeforeTheMonth()
    {
        var h = new Harness(); // business date 7 Oct 2026
        h.Source.Items.AddRange([Row(6), Row(7, 0.25m), Row(8, 900m), Row(20, 50m, month: 9), Row(5, 0), Row(4, -20)]);
        var report = (await h.Service.ListAsync(h.Agent)).Value!;
        var customer = Assert.Single(report.Items);
        Assert.Equal((2026, 10), (report.ReportYear, report.ReportMonth));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (report.PeriodStart, report.PeriodEnd));
        Assert.Equal(new DateOnly(2026, 10, 31), h.Source.ThroughDate);
        Assert.Equal(1000.25m, customer.DueAmount);   // includes the 8 Oct row, which is Upcoming by calendar date
        Assert.Equal(50m, customer.OverdueAmount);
        Assert.Equal(1050.25m, customer.TotalAmount);
        Assert.Equal(4, customer.Instalments.Count);
        Assert.True(customer.HasDue && customer.HasOverdue);
    }

    [Fact]
    public async Task MonthBoundaries_Sep30IsOverdue_Oct1AndOct31AreDue_Nov1IsOutside()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(30, 1m, unit: "A", month: 9), Row(1, 2m, unit: "B"), Row(31, 4m, unit: "C"), Row(1, 8m, unit: "D", month: 11)]);
        var items = (await h.Service.ListAsync(h.Agent)).Value!.Items;
        Assert.Equal(3, items.Count); // D is outside the month and, having no other row, is not listed
        var a = items.Single(c => c.UnitCode == "A");
        Assert.Equal((0m, 1m), (a.DueAmount, a.OverdueAmount));
        Assert.Equal("OverDue", a.Instalments[0].ReceivablesType);
        var b = items.Single(c => c.UnitCode == "B");
        Assert.Equal((2m, 0m), (b.DueAmount, b.OverdueAmount));
        var c31 = items.Single(c => c.UnitCode == "C");
        Assert.Equal((4m, 0m, "Due", "Upcoming"), (c31.DueAmount, c31.OverdueAmount, c31.Instalments[0].ReceivablesType, c31.Instalments[0].DueTiming));
    }

    [Theory]
    [InlineData(2026, 2, 28)]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 12, 31)]
    [InlineData(2026, 4, 30)]
    public async Task SelectedMonthEndIsPassedToTheSource_AndLastDayIsDue(int year, int month, int lastDay)
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(lastDay, 5m, month: month, year: year), Row(1, 7m, unit: "X", month: month % 12 + 1, year: month == 12 ? year + 1 : year)]);
        var items = (await h.Service.ListAsync(h.Agent, year: year, month: month)).Value!.Items;
        Assert.Equal(new DateOnly(year, month, lastDay), h.Source.ThroughDate);
        var only = Assert.Single(items);
        Assert.Equal(5m, only.DueAmount);
    }

    [Fact]
    public async Task PastMonth_OverdueIsBeforeThatMonth_AndDailyTimingStaysIndependent()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(31, 3m, month: 8), Row(1, 4m, month: 9), Row(30, 5m, month: 9)]);
        var c = Assert.Single((await h.Service.ListAsync(h.Agent, year: 2026, month: 9)).Value!.Items);
        Assert.Equal((4m + 5m, 3m, 12m), (c.DueAmount, c.OverdueAmount, c.TotalAmount));
        // Relative to the real business date (7 Oct) all three are past dates: calendar timing is Overdue for each.
        Assert.All(c.Instalments, i => Assert.Equal("Overdue", i.DueTiming));
        Assert.Equal(["OverDue", "Due", "Due"], c.Instalments.Select(i => i.ReceivablesType));
    }

    [Theory]
    [InlineData(2026, null)]
    [InlineData(null, 5)]
    [InlineData(2026, 13)]
    [InlineData(2026, 0)]
    [InlineData(1999, 5)]
    public async Task InvalidReportingMonthIsRejectedWithoutReadingPact(int? year, int? month)
    {
        var h = new Harness();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, year: year, month: month)).Outcome);
        Assert.Equal(0, h.Source.Reads);
    }

    [Fact]
    public async Task ExcludedLegacyCategoriesAreNotRemoved_HandoverLegalAndChequeUnitsStayListed()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(6, unit: "TP121-101", month: 9, year: 2026) with { FullName = "*Cancelled" }, Row(5, unit: "TP103-1")]);
        Assert.Equal(2, (await h.Service.ListAsync(h.Agent)).Value!.TotalCount);
    }

    [Fact]
    public async Task EachApartmentIsItsOwnRow_WithSeparateDueOverdueAndTotal()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(7, 40m), Row(6, 60m, month: 9), Row(5, 25m, unit: "TP140-202", month: 9), Row(5, 900m, unit: "TP140-303", month: 11), Row(6, 0m, unit: "TP140-404")]);
        var items = (await h.Service.ListAsync(h.Agent)).Value!.Items;
        Assert.Equal(2, items.Count);
        var a = items.Single(c => c.UnitCode == "TP140-101");
        Assert.Equal((40m, 60m, 100m), (a.DueAmount, a.OverdueAmount, a.TotalAmount));
        var b = items.Single(c => c.UnitCode == "TP140-202");
        Assert.Equal((0m, 25m, 25m), (b.DueAmount, b.OverdueAmount, b.TotalAmount));
    }

    [Fact]
    public async Task AmbiguousBucketHasNoTotal_ButApartmentStaysVisible()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(6, month: 9), Row(6, voucher: "INV-2", month: 9)]);
        var c = Assert.Single((await h.Service.ListAsync(h.Agent)).Value!.Items);
        Assert.Null(c.OverdueAmount);
        Assert.Null(c.TotalAmount);
        Assert.Equal("NeedsReview", c.AmountStatus);
        Assert.Equal(2, c.Instalments.Count);
    }

    [Fact]
    public async Task TheRemainingAmountDecidesThePaymentStatus_AndAPaidStatusWithARemainderIsContradictory()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(5) with { SourceStatus = "Installment" }, Row(6, voucher: "P") with { SourceStatus = "Paid" }]);
        var c = Assert.Single((await h.Service.ListAsync(h.Agent)).Value!.Items);
        var by = c.Instalments.ToDictionary(i => i.DueDate.Day);
        Assert.Equal("Unpaid", by[5].PaymentStatus);                                       // a positive remainder is unpaid, whatever the text says
        Assert.Equal("Unknown", by[6].PaymentStatus);                                      // Paid + positive remainder is contradictory
        Assert.Equal(["Installment", "Paid"], c.Instalments.Select(i => i.SourceStatus));
        var none = new Dictionary<string, string>();
        Assert.Equal("Paid", PactReceivableCustomersAppService.PaymentStatus("Paid", 0m, none));
        Assert.Equal("Paid", PactReceivableCustomersAppService.PaymentStatus("Installment", 0m, none));
        Assert.Equal("Unpaid", PactReceivableCustomersAppService.PaymentStatus("Installment", 10m, none));
        Assert.Equal("Unpaid", PactReceivableCustomersAppService.PaymentStatus(null, 10m, none));
        Assert.Equal("Unknown", PactReceivableCustomersAppService.PaymentStatus("Installment", null, none));
        Assert.Equal("Unknown", PactReceivableCustomersAppService.PaymentStatus("Installment", -5m, none));
    }

    [Theory]
    [InlineData(2026, 10, 31)]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 12, 31)]
    public void SqlDatetimeEndDate_IsExactlyRepresentable_AndNeverReachesNextDay(int y, int m, int d)
    {
        var end = PactSqlReceivablesSource.EndOfDay(new DateOnly(y, m, d));
        Assert.Equal(new DateTime(y, m, d, 23, 59, 59, 997), end);
        Assert.Equal(end, new System.Data.SqlTypes.SqlDateTime(end).Value);            // survives SQL datetime rounding
        var next = new DateTime(y, m, d).AddDays(1);
        Assert.True(end < next);
        Assert.Equal(next, new System.Data.SqlTypes.SqlDateTime(new DateTime(y, m, d, 23, 59, 59, 999)).Value); // .999 would round into the next day
        Assert.True(new DateTime(y, m, d, 23, 59, 59, 997) <= end && next > end);      // a due date at 23:59:59.997 is in, next midnight is out
    }

    [Fact]
    public async Task InstalmentsCarryTypeTimingAndPaymentStatusSeparately_AndUnmappedStatusIsUnknown()
    {
        var h = new Harness();
        h.SqlOptions.SourceStatusMap["Part"] = "PartiallyPaid";
        h.SqlOptions.SourceStatusMap["Open"] = "Unpaid";
        h.SqlOptions.SourceStatusMap["Closed"] = "Paid";
        h.Source.Items.AddRange([Row(5) with { SourceStatus = "Part" }, Row(7) with { SourceStatus = "Open" },
            Row(9, 77m) with { SourceStatus = "Open" }, Row(6, voucher: "X") with { SourceStatus = "Closed" },
            Row(4, voucher: "Y") with { SourceStatus = "Weird" }]);
        var c = Assert.Single((await h.Service.ListAsync(h.Agent)).Value!.Items);
        var by = c.Instalments.ToDictionary(i => i.DueDate.Day);
        Assert.Equal(("Due", "PartiallyPaid", "Overdue"), (by[5].ReceivablesType, by[5].PaymentStatus, by[5].DueTiming));
        Assert.Equal(("Unpaid", "DueToday"), (by[7].PaymentStatus, by[7].DueTiming));
        Assert.Equal(("Due", "Unpaid", "Upcoming"), (by[9].ReceivablesType, by[9].PaymentStatus, by[9].DueTiming));
        Assert.Equal("Unknown", by[6].PaymentStatus); // mapped Paid but a remainder is owed
        Assert.Equal(("Unpaid", "Weird"), (by[4].PaymentStatus, by[4].SourceStatus));   // an unmapped text does not matter: the remainder decides
        Assert.Equal(477m, c.DueAmount);
    }

    [Theory]
    [InlineData("due", 1)]
    [InlineData("overdue", 2)]
    [InlineData("all", 3)]
    public async Task DueAndOverdueAreAUnion_NotARequirementToHaveBoth(string status, int expected)
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(7), Row(6, tenant: "3002", month: 9), Row(5, tenant: "3003", month: 9)]);
        Assert.Equal(expected, (await h.Service.ListAsync(h.Agent, status: status)).Value!.TotalCount);
    }

    [Theory]
    [InlineData(120, 150)]
    [InlineData(1, 31)]
    [InlineData(999, 330)]   // command timeout is clamped to 300 s
    public void RequestBudgetOutlivesTheSqlCommandTimeout(int commandTimeout, int expectedBudget)
    {
        var options = new PactReceivablesOptions { CommandTimeoutSeconds = commandTimeout };
        Assert.Equal(expectedBudget, options.RequestBudgetSeconds);
        // A slow procedure must hit the SQL command timeout (diagnosable) before the shared budget token cancels it.
        Assert.True(options.RequestBudgetSeconds > Math.Clamp(commandTimeout, 1, 300));
        Assert.Equal(120, new PactReceivablesOptions().CommandTimeoutSeconds);
    }

    [Fact]
    public async Task BudgetCancellation_IsReportedAsUnavailable_ButCallerCancellationPropagates()
    {
        var h = new Harness();
        h.Source.Delay = _ => throw new TaskCanceledException("budget elapsed");   // what ExecuteReaderAsync throws when the budget token fires
        var result = await h.Service.ListAsync(h.Agent);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Contains("timed out", result.Detail);

        using var caller = new CancellationTokenSource();
        h.Source.Delay = async token => { caller.Cancel(); await Task.Delay(Timeout.Infinite, token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.ListAsync(h.Agent, cancellationToken: caller.Token));
    }

    [Theory]
    [InlineData(4, "", "dbo.p4AccountReceivables")]
    [InlineData(32, "V2", "dbo.p32AccountReceivablesV2")]
    public void ProcedureNamesAreAllowListed(int company, string suffix, string expected) =>
        Assert.Equal(expected, PactSqlReceivablesSource.ProcedureName(company, suffix));

    [Theory]
    [InlineData(4, "V2; DROP TABLE x")]
    [InlineData(4, "..x")]
    [InlineData(7, "")]
    public void InvalidProcedureConfigurationIsRejected(int company, string suffix) =>
        Assert.Throws<PactReceivablesSourceException>(() => PactSqlReceivablesSource.ProcedureName(company, suffix));

    [Fact]
    public void LegacyExclusionsAreOffByDefault() => Assert.False(new PactReceivablesOptions().ApplyLegacyExclusions);

    [Fact]
    public async Task CompanyAndTenantFormTheIdentity_AndPagingIsAfterCustomerGrouping()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(1), Row(2), Row(3, company: 32), Row(4, tenant: "4001")]);
        var first = (await h.Service.ListAsync(h.Agent, pageSize: 2)).Value!;
        var second = (await h.Service.ListAsync(h.Agent, page: 2, pageSize: 2)).Value!;
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Equal(2, first.Items.Single(c => c.CompanyId == 4 && c.TenantId == "3001").Instalments.Count);
        Assert.Single(first.Items, c => c.CompanyId == 32 && c.TenantId == "3001");
    }

    [Fact]
    public async Task PhoneSearchHandlesFormatting_CompanyFilterDoesNotMixCompanyIds()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(6), Row(5, company: 32)]);
        var report = (await h.Service.ListAsync(h.Agent, companyId: 32, search: "971500003001")).Value!;
        Assert.Equal(32, Assert.Single(report.Items).CompanyId);
    }

    [Fact]
    public async Task UnauthorizedDisabledAndInvalidRequestsDoNotTouchPACT()
    {
        var h = new Harness();
        var reporter = new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []);
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Service.ListAsync(reporter)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, companyId: 25)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, page: int.MaxValue)).Outcome);
        h.SqlOptions.Enabled = false;
        Assert.Equal(CollectionsOutcome.Disabled, (await h.Service.ListAsync(h.Agent)).Outcome);
        Assert.Equal(0, h.Source.Reads);
    }

    [Fact]
    public async Task SourceFailureIsUnavailable_NotAnEmptyOrPartialList()
    {
        var h = new Harness();
        h.Source.Failure = new PactReceivablesSourceException("CRM exclusions not configured.");
        var result = await h.Service.ListAsync(h.Agent);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public void LegacyExclusionsKeepCutoffBoundariesAndDoNotActivateTheCommentedTP119Rule()
    {
        var options = new PactReceivablesOptions
        {
            SpecialCaseUnitCodes = ["TP140-special"], PdcExcludedUnitCodes = ["TP140-pdc"]
        };
        var boundary = Row(6, unit: "TP121-101") with { DueDate = new DateTime(2025, 12, 15) };
        var after = boundary with { DueDate = new DateTime(2025, 12, 16) };
        var handover = Row(5, unit: "TP140-handover");
        var rows = new[] { boundary, after, handover, Row(6, unit: "TP103-1"),
            Row(6, unit: "TP140-special"), Row(6, unit: "TP140-pdc"),
            Row(6) with { FullName = "*Hidden" }, Row(6, unit: "TP119-101"), Row(6, unit: "TP140-kept") };
        var kept = PactSqlReceivablesSource.ApplyExclusions(rows,
            new HashSet<(string, DateTime)> { (handover.UnitCode, handover.DueDate) }, options).ToList();
        Assert.Equal(3, kept.Count);
        Assert.Contains(boundary, kept);
        Assert.Contains(kept, r => r.UnitCode == "TP119-101");
        Assert.Contains(kept, r => r.UnitCode == "TP140-kept");
    }
}
