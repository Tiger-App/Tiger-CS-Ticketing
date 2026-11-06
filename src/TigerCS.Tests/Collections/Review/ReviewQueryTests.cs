using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

public sealed class ReviewQueryTests
{
    private const string CurrentMonth = "CurrentMonth";

    private static async Task<ReviewHarness> SeededAsync()
    {
        var h = new ReviewHarness();
        h.Source.Items.AddRange([
            ReviewHarness.Row(1, 500m),                                                // unpaid, ready
            ReviewHarness.Row(2, 200m, plan: 500m, company: 32, project: "TP150"),     // partly paid (Unpaid: the remainder decides), company 32
            ReviewHarness.Row(3, 50m),                                                 // below the AED 100 default
            ReviewHarness.Row(4, 800m, plan: 500m) with { AllocatedAmount = 100m },    // remaining above the original: the source contradicts itself, status unknown
            ReviewHarness.Row(5, 600m, mobile: ""),                                    // no callable phone
            ReviewHarness.Row(6, 1000m, day: 5, name: "Zainab Al Ali"),                // earlier in the month
        ]);
        await h.RefreshAsync();
        return h;
    }

    private static async Task<ReviewPageDto> Query(ReviewHarness h, ReviewFilter filter, int page = 1, int size = 25)
    {
        using var scope = h.NewScope();
        var result = await scope.Query.QueryAsync(h.Manager, filter, page, size, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Detail);
        return result.Value!;
    }

    private static string[] Numbers(ReviewPageDto page) => page.Items.Select(i => i.CustomerName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task DefaultView_IsConfirmedUnpaidOrPartiallyPaid_AtLeastTheDefaultMinimum()
    {
        using var h = await SeededAsync();
        var page = await Query(h, new ReviewFilter(ReminderType: CurrentMonth));
        Assert.Equal(100m, page.MinRemainingApplied);
        // 3 (AED 50) is under the minimum; 4 has an unknown payment status. 5 is Unpaid but cannot be called.
        Assert.Equal(["Customer 1", "Customer 2", "Customer 5", "Zainab Al Ali"], Numbers(page));
        Assert.Equal(1, page.Counts.UnknownPaymentStatus);   // the hidden unknown is announced, not lost
    }

    [Fact]
    public async Task UnknownStatusStaysVisible_ButIsNeverSelectable()
    {
        using var h = await SeededAsync();
        var unknown = await Query(h, new ReviewFilter(PaymentStatus: "Unknown", ReminderType: CurrentMonth));
        var row = Assert.Single(unknown.Items);
        Assert.Equal("Customer 4", row.CustomerName);
        Assert.Equal("NeedsReview", row.ValidationStatus);
        Assert.Contains(row.Reasons, r => r.Code == ReviewReasons.PaymentStatusUnknown && r.Explanation.Length > 20);
        using var scope = h.NewScope();
        var selected = await scope.Query.ResolveAsync(new SelectionRequest(null, ReviewQueryService.ModeSelectedKeys, [row.RecordKey]), CancellationToken.None);
        Assert.Empty(selected.Records);
    }

    [Fact]
    public async Task PaymentStatusFilter_AllUnpaidPartiallyPaidPaidUnknown()
    {
        using var h = await SeededAsync();
        var all = await Query(h, new ReviewFilter(PaymentStatus: "All", MinRemaining: 0m, ReminderType: CurrentMonth));
        Assert.Equal(6, all.TotalCount);
        Assert.Empty((await Query(h, new ReviewFilter(PaymentStatus: "PartiallyPaid", ReminderType: CurrentMonth))).Items);   // partly paid is Unpaid: the remainder decides
        Assert.Equal(["Customer 1", "Customer 2", "Customer 3", "Customer 5", "Zainab Al Ali"],
            Numbers(await Query(h, new ReviewFilter(PaymentStatus: "Unpaid", MinRemaining: 0m, ReminderType: CurrentMonth))));
        Assert.Empty((await Query(h, new ReviewFilter(PaymentStatus: "Paid", MinRemaining: 0m, ReminderType: CurrentMonth))).Items);
        Assert.Equal(["Customer 4"], Numbers(await Query(h, new ReviewFilter(PaymentStatus: "Unknown", ReminderType: CurrentMonth))));
    }

    [Fact]
    public async Task MinimumAndMaximumRemainingAmount_AreEditable()
    {
        using var h = await SeededAsync();
        Assert.Equal(["Customer 1", "Customer 5", "Zainab Al Ali"], Numbers(await Query(h, new ReviewFilter(MinRemaining: 500m, ReminderType: CurrentMonth))));
        Assert.Equal(["Customer 1", "Customer 5"], Numbers(await Query(h, new ReviewFilter(MinRemaining: 500m, MaxRemaining: 600m, ReminderType: CurrentMonth))));
        Assert.Contains("Customer 3", Numbers(await Query(h, new ReviewFilter(MinRemaining: 0m, ReminderType: CurrentMonth, PaymentStatus: "All"))));
        Assert.Equal(100m, (await Query(h, new ReviewFilter())).MinRemainingApplied);
    }

    [Fact]
    public async Task CompanyProjectUnitAndCustomerFilters()
    {
        using var h = await SeededAsync();
        Assert.Equal(["Customer 2"], Numbers(await Query(h, new ReviewFilter(CompanyId: 32, ReminderType: CurrentMonth))));
        Assert.Equal(["Customer 2"], Numbers(await Query(h, new ReviewFilter(Project: "tp15", ReminderType: CurrentMonth))));
        Assert.Equal(["Customer 5"], Numbers(await Query(h, new ReviewFilter(Unit: "TP140-5", ReminderType: CurrentMonth))));
        Assert.Equal(["Zainab Al Ali"], Numbers(await Query(h, new ReviewFilter(Customer: "zainab", ReminderType: CurrentMonth))));
        // A phone fragment in any common format finds the customer.
        Assert.Equal(["Customer 1"], Numbers(await Query(h, new ReviewFilter(Customer: "050-0000001", ReminderType: CurrentMonth))));
        Assert.Equal(["Customer 1"], Numbers(await Query(h, new ReviewFilter(Customer: "+971500000001", ReminderType: CurrentMonth))));
        Assert.Empty((await Query(h, new ReviewFilter(Customer: "50%", ReminderType: CurrentMonth))).Items);   // wildcards are literal
    }

    [Fact]
    public async Task YearMonthAndCustomDueDateRange()
    {
        using var h = await SeededAsync();
        Assert.Equal(4, (await Query(h, new ReviewFilter(Year: 2026, Month: 10, ReminderType: CurrentMonth))).TotalCount);
        Assert.Equal(0, (await Query(h, new ReviewFilter(Year: 2026, Month: 9, ReminderType: CurrentMonth))).TotalCount);
        Assert.Equal(["Zainab Al Ali"], Numbers(await Query(h, new ReviewFilter(DueFrom: new(2026, 10, 1), DueTo: new(2026, 10, 10), ReminderType: CurrentMonth))));
        // Year/month and a custom range intersect.
        Assert.Equal(["Customer 1", "Customer 2", "Customer 5"],
            Numbers(await Query(h, new ReviewFilter(Year: 2026, Month: 10, DueFrom: new(2026, 10, 15), ReminderType: CurrentMonth))));
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(Year: 2026), 1, 25, CancellationToken.None)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(DueFrom: new(2026, 10, 9), DueTo: new(2026, 10, 1)), 1, 25, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ReminderTypeFilter_SeparatesTheStages()
    {
        using var h = await SeededAsync();
        var follow = await Query(h, new ReviewFilter(ReminderType: "Follow Up", PaymentStatus: "All", MinRemaining: 0m));
        Assert.Equal(6, follow.TotalCount);
        Assert.All(follow.Items, i => Assert.Equal("FollowUp", i.ReminderType));
        Assert.Equal(0, (await Query(h, new ReviewFilter(ReminderType: "LegalCase", PaymentStatus: "All", MinRemaining: 0m))).TotalCount);
    }

    [Fact]
    public async Task ValidationStatusFilter_AndCountsAddUp()
    {
        using var h = await SeededAsync();
        var filter = new ReviewFilter(ReminderType: CurrentMonth);
        var counts = (await Query(h, filter)).Counts;
        Assert.Equal(3, counts.Ready);                 // 1, 2 and Zainab
        Assert.Equal(1, counts.NeedsReview);           // 5: no phone
        Assert.Equal(0, counts.Excluded);
        Assert.Equal(0, counts.AlreadySent);
        Assert.Equal(counts.Total, counts.Ready + counts.NeedsReview + counts.Excluded + counts.AlreadySent);
        Assert.Equal(3, (await Query(h, filter with { ValidationStatus = "Ready" })).TotalCount);
        Assert.Equal(["Customer 5"], Numbers(await Query(h, filter with { ValidationStatus = "Needs Review" })));
        // The counts ignore the status filter itself so the four tiles stay comparable.
        Assert.Equal(counts, (await Query(h, filter with { ValidationStatus = "NeedsReview" })).Counts);
        // Follow Up is scheduled for day 28, so today those records are Excluded (policy), except where a data problem already needs review.
        Assert.Equal(["Customer 1", "Customer 2", "Zainab Al Ali"],
            Numbers(await Query(h, new ReviewFilter(ReminderType: "FollowUp", ValidationStatus: "Excluded"))));
    }

    [Fact]
    public async Task ReasonFilter_FindsRecordsByTheirFailedValidation_WithPlainLanguage()
    {
        using var h = await SeededAsync();
        var page = await Query(h, new ReviewFilter(Reason: ReviewReasons.NoValidContact, ReminderType: CurrentMonth));
        var row = Assert.Single(page.Items);
        Assert.Equal("Customer 5", row.CustomerName);
        Assert.Contains("phone number", row.Reasons.Single(r => r.Code == ReviewReasons.NoValidContact).Explanation);
        Assert.Equal("PACT receivables (companies 4 and 32)", row.Source);
        Assert.Equal(ReviewHarness.Now, row.SourceReadAtUtc);
        Assert.Equal(CollectionsOutcome.InvalidRequest,
            (await h.NewScope().Query.QueryAsync(h.Manager, new ReviewFilter(Reason: "Nonsense"), 1, 25, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task CountsAndPaginationRunInTheDatabase_NeverTheFinancialSource()
    {
        using var h = new ReviewHarness();
        for (var n = 1; n <= 60; n++) h.Source.Items.Add(ReviewHarness.Row(n));
        await h.RefreshAsync();
        var reads = h.Source.Reads;
        var filter = new ReviewFilter(ReminderType: CurrentMonth);
        var first = await Query(h, filter, 1, 25);
        var third = await Query(h, filter, 3, 25);
        Assert.Equal(60, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(10, third.Items.Count);
        Assert.Empty(first.Items.Select(i => i.RecordKey).Intersect(third.Items.Select(i => i.RecordKey)));
        Assert.Equal(60, first.Counts.Ready);
        Assert.Equal(reads, h.Source.Reads);      // no PACT call to render any page
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Query.QueryAsync(h.Manager, filter, 1, 101, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task StaleData_CannotBeReady_AndSaysWhy()
    {
        using var h = await SeededAsync();
        h.Time.Advance(TimeSpan.FromMinutes(61));
        var page = await Query(h, new ReviewFilter(ReminderType: CurrentMonth));
        Assert.Equal(0, page.Counts.Ready);
        Assert.Equal(4, page.Counts.NeedsReview);
        Assert.True(page.Run!.IsStale);
        Assert.All(page.Items.Where(i => i.CustomerName != "Customer 5"), i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.StaleSource));
    }

    [Fact]
    public async Task ReviewScreenData_RequiresFinancialReadAuthorization()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Query.QueryAsync(h.Reporting, new ReviewFilter(), 1, 25, CancellationToken.None)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Refresh.StartAsync(h.Reporting, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, h.Source.Reads - 1);   // only the seeding refresh ever read the source
        // A financial reader can look, but selecting contacts to send is a separate grant.
        Assert.True((await scope.Query.QueryAsync(h.Agent, new ReviewFilter(), 1, 25, CancellationToken.None)).IsSuccess);
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Query.SummarizeAsync(h.Agent, h.All(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Refresh_IsExplicit_SingleFlight_AndReportsProgress()
    {
        using var h = new ReviewHarness();
        h.Source.Items.Add(ReviewHarness.Row(1));
        using var scope = h.NewScope();
        var first = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        var second = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, first.Outcome);
        Assert.Equal(CollectionsOutcome.Replayed, second.Outcome);                 // a second click joins the run in progress
        Assert.Equal(first.Value!.RunId, second.Value!.RunId);
        Assert.Single(h.Scheduler.Refreshes);
        Assert.Equal("Queued", first.Value.Status);
        Assert.Equal(0, h.Source.Reads);                                            // starting reads nothing
        var queued = await scope.Query.QueryAsync(h.Manager, new ReviewFilter(), 1, 25, CancellationToken.None);
        Assert.Null(queued.Value!.Run);                                              // nothing published yet

        await scope.Refresh.RunAsync(first.Value.RunId, CancellationToken.None);
        await scope.Refresh.RunAsync(first.Value.RunId, CancellationToken.None);   // a duplicate job execution does nothing
        Assert.Equal(1, h.Source.Reads);
        var done = (await scope.Query.GetCurrentRunAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.Equal("Completed", done.Status);
        Assert.Equal(100, done.ProgressPercent);
        Assert.True(done.IsCurrent);
        Assert.Equal(ReviewHarness.Now, done.SourceReadAtUtc);
        Assert.True(done.RecordCount >= 1);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheLastGoodData_AndReportsTheError()
    {
        using var h = await SeededAsync();
        var good = await Query(h, new ReviewFilter(ReminderType: CurrentMonth));
        h.Source.Failure = new PactReceivablesSourceException("The PACT report connection is not configured.");
        using var scope = h.NewScope();
        var started = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        await scope.Refresh.RunAsync(started.Value!.RunId, CancellationToken.None);
        var failed = (await scope.Query.GetRunAsync(h.Manager, started.Value.RunId, CancellationToken.None)).Value!;
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("not configured", failed.Error);
        Assert.False(failed.IsCurrent);
        Assert.Equal(good.TotalCount, (await Query(h, new ReviewFilter(ReminderType: CurrentMonth))).TotalCount);   // still reviewable
        // And a new refresh may be started afterwards.
        h.Source.Failure = null;
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task PublishingANewRun_ReplacesTheCurrentOne()
    {
        using var h = await SeededAsync();
        h.Source.Items.RemoveAll(r => r.TenantId == "T0001");
        var second = await h.RefreshAsync();
        Assert.True(second.IsCurrent);
        var page = await Query(h, new ReviewFilter(ReminderType: CurrentMonth));
        Assert.DoesNotContain("Customer 1", Numbers(page));
    }
}
