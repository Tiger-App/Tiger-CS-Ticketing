using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Month / range windows, payment status, minimum amount, Due/Overdue separation, totals and paging of the instalment list (fake-based).</summary>
public sealed class InstalmentFilterTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);   // 9 Oct 2026, Dubai

    private sealed class Harness
    {
        public FakeInstalmentSource Source { get; } = new(Now);
        public PactInstalmentsAppService Service { get; }
        public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);
        public Harness()
        {
            var options = new CollectionsOptions { Enabled = true };
            Service = new(options, new PactReceivablesOptions { Enabled = true }, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()),
                new CollectionsClock(options, new FakeTimeProvider(Now)), Source, NullLogger<PactInstalmentsAppService>.Instance);
        }
    }

    private static int seq;
    private static FakeInstalment Inst(DateTime due, decimal remaining, decimal? original = null, decimal? allocated = null, string unit = "TP124-1001", bool breakdown = true,
        string? status = null, int company = 4, string? tenant = null) =>
        new(company, tenant ?? "T" + ++seq, "Customer " + seq, 100 + seq, unit, "V" + seq, due, original, allocated, remaining, status ?? (remaining == 0 ? "Paid" : "Installment"), breakdown);

    private static DateTime D(int y, int m, int d, int h = 0, int mi = 0, int s = 0, int ms = 0) => new(y, m, d, h, mi, s, ms);

    // ---------- month / year selectors ----------
    [Theory]
    [InlineData(2026, 2, "2026-02-01", "2026-02-28")]
    [InlineData(2028, 2, "2028-02-01", "2028-02-29")]     // leap February
    [InlineData(2100, 2, "2100-02-01", "2100-02-28")]     // 2100 is not a leap year
    [InlineData(2026, 4, "2026-04-01", "2026-04-30")]
    [InlineData(2026, 12, "2026-12-01", "2026-12-31")]
    [InlineData(2027, 1, "2027-01-01", "2027-01-31")]
    public void MonthRange_IsTheFirstAndLastDayOfTheCalendarMonth(int year, int month, string from, string to)
    {
        var (f, t) = CollectionsDateRanges.Month(year, month);
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), (f, t));
        Assert.True(CollectionsDateRanges.TryAsCalendarMonth(f, t, out var y, out var m));
        Assert.Equal((year, month), (y, m));
    }

    [Theory]
    [InlineData("2026-02-02", "2026-02-28")]
    [InlineData("2026-02-01", "2026-02-27")]
    [InlineData("2026-02-01", "2026-03-31")]
    [InlineData("2025-12-15", "2026-01-14")]
    public void ACustomRangeIsNotACalendarMonth(string from, string to) => Assert.False(CollectionsDateRanges.TryAsCalendarMonth(DateOnly.Parse(from), DateOnly.Parse(to), out _, out _));

    [Fact]
    public async Task MonthWindow_IncludesTheLastMillisecondOfTheLastDay_AndExcludesTheNeighbours()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Inst(D(2028, 1, 31, 23, 59, 59, 997), 500m), Inst(D(2028, 2, 1), 500m), Inst(D(2028, 2, 29, 23, 59, 59, 997), 500m), Inst(D(2028, 3, 1), 500m)]);
        var (from, to) = CollectionsDateRanges.Month(2028, 2);
        var page = (await h.Service.ListAsync(h.Agent, dateFrom: from, dateTo: to, minAmount: 0)).Value!;
        Assert.Equal([new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)], page.Items.Select(i => i.DueDate));
        Assert.Equal(2, page.Totals.Count);
    }

    [Fact]
    public async Task YearChange_DecemberAndJanuaryAreSeparateMonths_AndARangeAcrossThemCoversBoth()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Inst(D(2026, 12, 31), 500m), Inst(D(2027, 1, 1), 500m)]);
        Assert.Single((await h.Service.ListAsync(h.Agent, dateFrom: new(2026, 12, 1), dateTo: new(2026, 12, 31), minAmount: 0)).Value!.Items);
        Assert.Single((await h.Service.ListAsync(h.Agent, dateFrom: new(2027, 1, 1), dateTo: new(2027, 1, 31), minAmount: 0)).Value!.Items);
        Assert.Equal(2, (await h.Service.ListAsync(h.Agent, dateFrom: new(2026, 12, 1), dateTo: new(2027, 1, 31), minAmount: 0)).Value!.Totals.Count);
    }

    [Fact]
    public async Task DefaultWindowIsJanuaryFirstToTheEndOfTheCurrentMonth_AndBothDatesStayEditable()
    {
        var h = new Harness();
        var page = (await h.Service.ListAsync(h.Agent)).Value!;
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31)), (page.DateFrom, page.DateTo));
        var custom = (await h.Service.ListAsync(h.Agent, dateFrom: new(2019, 3, 5), dateTo: new(2027, 8, 20))).Value!;
        Assert.Equal((new DateOnly(2019, 3, 5), new DateOnly(2027, 8, 20)), (custom.DateFrom, custom.DateTo));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, dateFrom: new(2026, 5, 2), dateTo: new(2026, 5, 1))).Outcome);
    }

    // ---------- payment status ----------
    [Theory]
    [InlineData(1000, 0, 1000, "Unpaid")]
    [InlineData(1000, 400, 600, "PartiallyPaid")]
    [InlineData(1000, 1000, 0, "FullyPaid")]
    [InlineData(1000, 1000, 999, "Unknown")]     // original - paid != remaining: an inconsistent breakdown is never trusted
    [InlineData(-5, 0, 0, "Unknown")]            // negative original
    [InlineData(1000, 400, 600.005, "PartiallyPaid")]   // within the 0.01 tolerance
    public void CompanionShape_ClassifiesFromOriginalPaidAndRemaining(double original, double allocated, double remaining, string expected) =>
        Assert.Equal(expected, CollectionsPaymentFilters.Classify((decimal)remaining, (decimal)original, (decimal)allocated, "Installment", true));

    [Fact]
    public void CompanionShape_MissingAmountsAreUnknown_NeverGuessed()
    {
        Assert.Equal("Unknown", CollectionsPaymentFilters.Classify(600m, 1000m, null, "Installment", true));
        Assert.Equal("Unknown", CollectionsPaymentFilters.Classify(600m, null, 400m, "Installment", true));
    }

    [Theory]
    [InlineData(0, "Paid", "FullyPaid")]
    [InlineData(0, "paid", "FullyPaid")]
    [InlineData(0, "Installment", "FullyPaid")]          // zero remaining is paid, whatever the source's text says
    [InlineData(250, "Installment", "Unpaid")]           // a positive remainder is unpaid (partly paid is not told apart without the breakdown)
    [InlineData(250, null, "Unpaid")]
    [InlineData(250, "Paid", "Unknown")]                 // contradictory: the source says Paid but a remainder is owed
    [InlineData(-1, "Installment", "Unknown")]           // a negative remainder is invalid
    public void DeployedShape_TheRemainingAmountDecides_AndContradictionsNeedVerification(double remaining, string? sourceStatus, string expected) =>
        Assert.Equal(expected, CollectionsPaymentFilters.Classify((decimal)remaining, null, null, sourceStatus, false));

    [Fact]
    public void AMissingRemainingAmount_IsUnknown_InEitherShape()
    {
        Assert.Equal("Unknown", CollectionsPaymentFilters.Classify(null, null, null, "Installment", false));
        Assert.Equal("Unknown", CollectionsPaymentFilters.Classify(null, 1000m, 400m, "Installment", true));
        Assert.Equal("Unknown", CollectionsPaymentFilters.Classify(600m, 1000m, 400m, "Paid", true));     // the breakdown adds up, but the source says Paid: contradictory
    }

    private static Harness Seed()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([
            Inst(D(2026, 9, 10), 1000m, 1000m, 0m),                              // unpaid, overdue
            Inst(D(2026, 9, 12), 600m, 1000m, 400m),                             // partially paid, overdue
            Inst(D(2026, 10, 20), 250m, 500m, 250m),                             // partially paid, due (current month, future day)
            Inst(D(2026, 11, 15), 700m, 700m, 0m),                               // unpaid, not yet due
            Inst(D(2026, 9, 1), 0m, 800m, 800m),                                 // fully paid
            Inst(D(2026, 10, 5), 0m, 300m, 300m),                                // fully paid
            Inst(D(2026, 9, 20), 90m, 400m, 310m)]);                             // partially paid, 90 remaining (below 100)
        return h;
    }

    private static async Task<TigerCS.Application.Modules.Collections.Dto.PactInstalmentsPageDto> List(Harness h, string status, decimal? min = null, int page = 1, int size = 25) =>
        (await h.Service.ListAsync(h.Agent, dateFrom: new(2026, 1, 1), dateTo: new(2026, 12, 31), paymentStatus: status, minAmount: min, page: page, pageSize: size)).Value!;

    [Fact]
    public async Task Outstanding_IsUnpaidPlusPartiallyPaid_AndNeverFullyPaid()
    {
        var h = Seed();
        var outstanding = await List(h, "outstanding", 0);
        Assert.Equal(5, outstanding.Totals.Count);
        Assert.All(outstanding.Items, i => Assert.True(i.RemainingAmount > 0));
        var unpaid = await List(h, "unpaid", 0); var partial = await List(h, "partial", 0);
        Assert.Equal(2, unpaid.Totals.Count); Assert.Equal(3, partial.Totals.Count);
        Assert.Equal(outstanding.Totals.Count, unpaid.Totals.Count + partial.Totals.Count);
        Assert.Equal(outstanding.Totals.RemainingTotal, unpaid.Totals.RemainingTotal + partial.Totals.RemainingTotal);
        Assert.All(unpaid.Items, i => Assert.Equal("Unpaid", i.PaymentStatus));
        Assert.All(partial.Items, i => Assert.Equal("PartiallyPaid", i.PaymentStatus));
        var paid = await List(h, "paid", 0);
        Assert.Equal(2, paid.Totals.Count);
        Assert.All(paid.Items, i => { Assert.Equal("FullyPaid", i.PaymentStatus); Assert.Equal(0m, i.RemainingAmount); Assert.Equal("NotApplicable", i.Classification); });
        Assert.Equal(7, (await List(h, "all", 0)).Totals.Count);
    }

    [Fact]
    public async Task PaymentStatusIsSeparateFromDueOverdue_APartiallyPaidInstalmentCanBeOverdue()
    {
        var h = Seed();
        var rows = (await List(h, "outstanding", 0)).Items;
        var partialOverdue = rows.Single(i => i.OriginalAmount == 1000m && i.PaidAmount == 400m);
        Assert.Equal(("PartiallyPaid", "Overdue"), (partialOverdue.PaymentStatus, partialOverdue.Classification));
        Assert.Equal(("PartiallyPaid", "Due"), rows.Where(i => i.PaidAmount == 250m).Select(i => (i.PaymentStatus, i.Classification)).Single());
        var future = rows.Single(i => i.DueDate == new DateOnly(2026, 11, 15));
        Assert.Equal(("Unpaid", "NotYetDue"), (future.PaymentStatus, future.Classification));       // a future instalment is never overdue
        Assert.Equal(("Unpaid", "Overdue"), rows.Where(i => i.RemainingAmount == 1000m).Select(i => (i.PaymentStatus, i.Classification)).Single());
    }

    [Fact]
    public async Task DueOverdueTotals_UseTheEstablishedMonthRule_AndExcludePaidRows()
    {
        var h = Seed();
        var totals = (await List(h, "all", 0)).Totals;
        Assert.Equal((3, 1690m), (totals.OverdueCount, totals.OverdueRemaining));      // 10 Sep 1000 + 12 Sep 600 + 20 Sep 90 (before October)
        Assert.Equal((1, 250m), (totals.DueCount, totals.DueRemaining));               // 20 Oct, current month
        Assert.Equal((1, 700m), (totals.NotYetDueCount, totals.NotYetDueRemaining));   // 15 Nov
        Assert.Equal(2, totals.FullyPaidCount);
    }

    // ---------- minimum outstanding amount ----------
    [Fact]
    public async Task Minimum_IsInclusive_ComparesTheRemainingBalance_AndAppliesToRowsCountsAndTotals()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Inst(D(2026, 9, 1), 99.99m, 200m, 100.01m), Inst(D(2026, 9, 2), 100m, 200m, 100m), Inst(D(2026, 9, 3), 100.01m, 200m, 99.99m), Inst(D(2026, 9, 4), 5000m, 5000m, 0m)]);
        var at100 = await List(h, "outstanding", 100m);
        Assert.Equal([100m, 100.01m, 5000m], at100.Items.Select(i => i.RemainingAmount));    // 100 itself is included, 99.99 is not
        Assert.Equal((3, 5200.01m), (at100.Totals.Count, at100.Totals.RemainingTotal));
        Assert.Equal(4, (await List(h, "outstanding", 0)).Totals.Count);
        Assert.Equal([5000m], (await List(h, "outstanding", 1000m)).Items.Select(i => i.RemainingAmount));
        Assert.Equal(1, (await List(h, "outstanding", 100.011m)).Totals.Count);                  // any other non-negative decimal is accepted
        Assert.True((await List(h, "outstanding", null)).MinAmount == 100m);                      // default is 100
        // Pages of the filtered set add up to the totals.
        var pages = new List<decimal>();
        for (var p = 1; p <= 2; p++) pages.AddRange((await List(h, "outstanding", 100m, p, 2)).Items.Select(i => i.RemainingAmount));
        Assert.Equal(at100.Totals.RemainingTotal, pages.Sum());
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("all")]
    public async Task Minimum_IsNotApplied_ToFullyPaidAndAll_SoPaidRowsAreNeverHidden(string status)
    {
        var h = Seed();
        var page = await List(h, status, 100000m);
        Assert.False(page.MinAmountApplied);
        Assert.Equal(status == "paid" ? 2 : 7, page.Totals.Count);                   // nothing is hidden by the huge minimum
        Assert.Contains(page.Items, i => i.PaymentStatus == "FullyPaid");
        Assert.Contains("not applied to Fully paid and All", string.Join(' ', page.Notes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Minimum_IsValidated()
    {
        var h = Seed();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, minAmount: -0.01m)).Outcome);
        Assert.True((await h.Service.ListAsync(h.Agent, minAmount: 0m)).IsSuccess);
    }

    // ---------- availability, unknown status, coverage ----------
    [Fact]
    public async Task FullyPaidAndAll_AreRefused_AgainstAnOutstandingOnlyDataset()
    {
        var h = Seed();
        h.Source.Companies[4] = FakeInstalmentSource.Loaded(4, Now, paidRetained: false, breakdown: true);
        h.Source.Companies[32] = FakeInstalmentSource.Loaded(32, Now, paidRetained: false, breakdown: true);
        foreach (var status in new[] { "paid", "all" })
        {
            var result = await h.Service.ListAsync(h.Agent, paymentStatus: status);
            Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
            Assert.Contains("not in the loaded data", result.Detail);
        }
        var ok = (await h.Service.ListAsync(h.Agent, paymentStatus: "outstanding")).Value!;
        Assert.False(ok.Views.FullyPaid); Assert.False(ok.Views.All); Assert.True(ok.Views.Outstanding);
    }

    [Fact]
    public async Task OnlyPartial_IsRefused_WhenTheSourceDoesNotReturnOriginalAndPaidAmounts_UnpaidFollowsTheRemainder()
    {
        var h = new Harness();
        h.Source.Companies[4] = FakeInstalmentSource.Loaded(4, Now, true, breakdown: false);
        h.Source.Companies[32] = FakeInstalmentSource.Loaded(32, Now, true, breakdown: false);
        h.Source.Rows.AddRange([Inst(D(2026, 9, 10), 250m, null, null, breakdown: false), Inst(D(2026, 9, 11), 0m, null, null, breakdown: false)]);
        Assert.Contains("cannot be told apart", (await h.Service.ListAsync(h.Agent, paymentStatus: "partial")).Detail);
        var outstanding = (await h.Service.ListAsync(h.Agent, minAmount: 0)).Value!;
        var row = Assert.Single(outstanding.Items);
        Assert.Equal(("Unpaid", null, null), (row.PaymentStatus, row.OriginalAmount, row.PaidAmount));        // the remainder decides; the original and paid amounts are not invented
        Assert.True(outstanding.Views.Unpaid); Assert.False(outstanding.Views.PartiallyPaid); Assert.True(outstanding.Views.FullyPaid);
        var unpaid = (await h.Service.ListAsync(h.Agent, paymentStatus: "unpaid", minAmount: 0)).Value!;
        Assert.Equal("Unpaid", Assert.Single(unpaid.Items).PaymentStatus);
        var paid = (await h.Service.ListAsync(h.Agent, paymentStatus: "paid")).Value!;
        Assert.Equal("FullyPaid", Assert.Single(paid.Items).PaymentStatus);
    }

    [Fact]
    public async Task BeforeTheFirstSnapshot_TheListIsAnExplicitNotLoadedState_WithTheWholeRangeAsAGap()
    {
        var h = new Harness();
        h.Source.Companies[4] = FakeSnapshotSource.NeverLoaded(4);
        h.Source.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        var page = (await h.Service.ListAsync(h.Agent, dateFrom: new(2026, 2, 1), dateTo: new(2026, 2, 28))).Value!;
        Assert.Empty(page.Items);
        Assert.True(page.Snapshot.NothingLoaded); Assert.False(page.Snapshot.RangeCovered); Assert.False(page.Snapshot.IsReady);
        Assert.All(page.Snapshot.Gaps, g => Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), (g.From, g.Through)));
        Assert.False(page.Views.FullyPaid);                     // "Fully paid" / "All" are not offered before any paid data exists
    }

    // ---------- totals / paging / tower ----------
    [Fact]
    public async Task TowerWindowStatusAndMinimum_ApplyTogether_ToRowsCountsTotalsAndPages()
    {
        var h = new Harness();
        for (var n = 1; n <= 60; n++)
            h.Source.Rows.Add(Inst(D(2026, 3, 1 + n % 28), n % 5 == 0 ? 0m : 50m + n * 10, 1000m, n % 5 == 0 ? 1000m : 1000m - (50m + n * 10), n % 2 == 0 ? "TP124-1" : "TP136-2", tenant: "t" + n));
        var all = await Page(h, towerId: 1, status: "outstanding", min: 200m, size: 7);
        var pageCount = (int)Math.Ceiling(all.Totals.Count / 7.0);
        var seen = new List<string>();
        for (var p = 1; p <= pageCount; p++) seen.AddRange((await Page(h, 1, "outstanding", 200m, 7, p)).Items.Select(i => i.TenantId));
        Assert.Equal(all.Totals.Count, seen.Count); Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.All((await Page(h, 1, "outstanding", 200m, 100)).Items, i => { Assert.Equal("124", i.TowerNumber); Assert.True(i.RemainingAmount >= 200m); });
        Assert.Equal(1, h.Source.LastRequest!.TowerId);
    }

    private static async Task<TigerCS.Application.Modules.Collections.Dto.PactInstalmentsPageDto> Page(Harness h, int? towerId, string status, decimal min, int size, int page = 1) =>
        (await h.Service.ListAsync(h.Agent, towerId: towerId, dateFrom: new(2026, 3, 1), dateTo: new(2026, 3, 31), paymentStatus: status, minAmount: min, page: page, pageSize: size)).Value!;

    [Fact]
    public async Task UnknownTowerAndInvalidStatus_AreRejected()
    {
        var h = Seed();
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Service.ListAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []))).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, paymentStatus: "banana")).Outcome);
        Assert.Equal(0, h.Source.Reads);                                              // neither an unauthorized nor a malformed request reads anything
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, towerId: 99)).Outcome);
    }

    [Fact]
    public async Task ThePageNeverTouchesPactOrARefresh()
    {
        // The service depends only on the local source abstraction: there is no refresher, loader or PACT type in its constructor.
        var parameters = typeof(PactInstalmentsAppService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name).ToList();
        Assert.DoesNotContain(parameters, n => n.Contains("Refresher") || n.Contains("Loader") || n.Contains("PactSql"));
        await Task.CompletedTask;
    }
}
