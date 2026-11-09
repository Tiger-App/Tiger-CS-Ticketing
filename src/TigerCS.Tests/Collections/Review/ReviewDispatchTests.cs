using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

public sealed class ReviewDispatchTests
{
    private static async Task<ReviewHarness> SeededAsync(int count = 3, bool file = false, Action<ReviewHarness>? configure = null)
    {
        var h = new ReviewHarness(file);
        configure?.Invoke(h);
        for (var n = 1; n <= count; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n));
        await h.RefreshAsync();
        return h;
    }

    private static async Task<CollectionsResult<DispatchDto>> ConfirmAsync(ReviewHarness h, ConfirmDispatchRequest request, CollectionsCaller? caller = null)
    {
        using var scope = h.NewScope();
        return await scope.Dispatch.ConfirmAsync(caller ?? h.Manager, request, CancellationToken.None);
    }

    private static async Task<DispatchDto> ApproveAndRunAsync(ReviewHarness h, string key = "k1", SelectionRequest? selection = null)
    {
        var (_, confirm) = await h.PrepareAsync(selection, key);
        var accepted = await ConfirmAsync(h, confirm);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
        return (await scope.Dispatch.GetAsync(h.Manager, accepted.Value!.DispatchId, CancellationToken.None)).Value!;
    }

    private static async Task<List<CollectionsDispatchItem>> ItemsAsync(ReviewHarness h)
    {
        using var context = h.CreateContext();
        return await context.CollectionsDispatchItems.AsNoTracking().OrderBy(i => i.RecordKey).ToListAsync();
    }

    // ------------------------------------------------------------ selection

    [Fact]
    public async Task SelectAllMatchingFilters_CoversEveryPage_NotJustTheVisibleOne()
    {
        using var h = await SeededAsync(60);
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, h.All(new ReviewFilter(ReminderType: "CurrentMonth")), CancellationToken.None)).Value!;
        Assert.Equal(60, summary.Count);                                    // page size is 25; the selection is exact, not per page
        Assert.Equal(60 * 100m + Enumerable.Range(1, 60).Sum(), summary.TotalsByCurrency["AED"]);
        Assert.Equal(60, summary.CountByReminderType["CurrentMonth"]);
        Assert.Equal(50, summary.Contacts.Count);                           // contact list is paged for display
        var second = (await scope.Query.SummarizeAsync(h.Manager, h.All(new ReviewFilter(ReminderType: "CurrentMonth")) with { ContactPage = 2 }, CancellationToken.None)).Value!;
        Assert.Equal(10, second.Contacts.Count);
        Assert.Empty(summary.Contacts.Select(c => c.RecordKey).Intersect(second.Contacts.Select(c => c.RecordKey)));
        Assert.All(summary.Contacts, c => Assert.StartsWith("+971", c.Phone));
    }

    [Fact]
    public async Task SelectionHonoursFiltersAndExplicitExclusions()
    {
        using var h = await SeededAsync(10);
        using var scope = h.NewScope();
        var filter = new ReviewFilter(ReminderType: "CurrentMonth", MinRemaining: 105m);
        var matching = (await scope.Query.SummarizeAsync(h.Manager, h.All(filter), CancellationToken.None)).Value!;
        Assert.Equal(6, matching.Count);                                    // 105..110
        var keys = matching.Contacts.Select(c => c.RecordKey).ToList();
        var minusTwo = (await scope.Query.SummarizeAsync(h.Manager, h.All(filter) with { ExcludedKeys = keys.Take(2).ToList() }, CancellationToken.None)).Value!;
        Assert.Equal(4, minusTwo.Count);
        var picked = (await scope.Query.SummarizeAsync(h.Manager,
            new SelectionRequest(null, ReviewQueryService.ModeSelectedKeys, keys.Take(3).ToList()), CancellationToken.None)).Value!;
        Assert.Equal(3, picked.Count);
        Assert.NotEqual(matching.Fingerprint, picked.Fingerprint);
    }

    [Fact]
    public async Task NotReadyRecordsCanNeverBeSelected_EvenByKey()
    {
        using var h = new ReviewHarness();
        h.Source.Items.AddRange([ReviewHarness.Row(1), ReviewHarness.Row(2, mobile: ""), ReviewHarness.Row(3, status: "Paid")]);          // a remainder the source contradicts (status Paid): unknown, never sendable
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", PaymentStatus: "All"), 1, 25, CancellationToken.None)).Value!;
        var summary = (await scope.Query.SummarizeAsync(h.Manager,
            new SelectionRequest(null, ReviewQueryService.ModeSelectedKeys, page.Items.Select(i => i.RecordKey).ToList()), CancellationToken.None)).Value!;
        Assert.Equal(1, summary.Count);
        Assert.Contains(summary.Notices, n => n.Contains("left out"));
    }

    [Fact]
    public async Task AnOversizeSelectionIsRefusedNotTruncated()
    {
        using var h = await SeededAsync(5, configure: x => x.Review.MaxSelection = 3);
        using var scope = h.NewScope();
        var result = await scope.Query.SummarizeAsync(h.Manager, h.All(new ReviewFilter(ReminderType: "CurrentMonth")), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
        Assert.Contains("Narrow the filters", result.Detail);
    }

    [Fact]
    public async Task TotalsAreSeparatedByCurrency()
    {
        using var h = await SeededAsync(2);
        using var scope = h.NewScope();
        var records = await scope.Context.CollectionsReviewRecords.AsNoTracking().Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToListAsync();
        records[0].Currency = "USD";
        var totals = ReviewQueryService.TotalsByCurrency(records);
        Assert.Equal(2, totals.Count);
        Assert.Equal(records[1].RemainingAmount, totals["AED"]);
        Assert.Equal(records[0].RemainingAmount, totals["USD"]);
    }

    // ------------------------------------------------------------ authorization & acknowledgements

    [Fact]
    public async Task OnlyAuthorizedUsersCanConfirm_AndNothingIsSent()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync();
        Assert.Equal(CollectionsOutcome.Forbidden, (await ConfirmAsync(h, confirm, h.Agent)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await ConfirmAsync(h, confirm, h.Reporting)).Outcome);
        Assert.Empty(await ItemsAsync(h));
        Assert.Empty(h.Scheduler.Dispatches);
        Assert.Empty(h.Client.Calls);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Dispatch.ReconcileAsync(h.Agent, Guid.NewGuid(), 1, new("ConfirmedUploaded", "x"), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ConfirmingNeedsTheActiveCampaignAcknowledgement_AndAnEnabledIntegration()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await ConfirmAsync(h, confirm with { AcknowledgeActiveCampaignRisk = false })).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await ConfirmAsync(h, confirm with { IdempotencyKey = " " })).Outcome);
        h.Genesys.Enabled = false;
        Assert.Equal(CollectionsOutcome.Disabled, (await ConfirmAsync(h, confirm)).Outcome);
        Assert.Empty(await ItemsAsync(h));
    }

    [Fact]
    public async Task MultipleUnitsOnOnePhone_AreSeparateRecords_EachSentOnItsOwn_NeverMerged()
    {
        using var h = new ReviewHarness();
        h.Source.Items.AddRange([ReviewHarness.Row(1, 300m, mobile: "0500003001"), ReviewHarness.Row(2, 450m, mobile: "0500003001"), ReviewHarness.Row(3)]);
        await h.RefreshAsync();
        var (summary, confirm) = await h.PrepareAsync();
        Assert.Equal(3, summary.Count);                                                    // every unit, the two on one number included
        Assert.Equal([300m, 450m, 500m], summary.Contacts.Select(c => c.Amount).Order());  // not merged into 750
        Assert.Equal((2, 1), (summary.SharedPhoneRecordCount, summary.SharedPhoneNumberCount));
        Assert.Contains(summary.Notices, n => n.Contains("Each unit is its own contact"));
        using var scope = h.NewScope();
        var overlaps = (await scope.Query.OverlapsAsync(h.Manager, 1, 25, CancellationToken.None)).Value!;
        Assert.Empty(overlaps.Groups);                                                     // a shared number is not an overlap any more
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, confirm)).Outcome);
        Assert.Equal(3, (await ItemsAsync(h)).Count);
    }

    [Fact]
    public async Task DayFourteenOverlap_OfCurrentMonthAndLegalNotice_IsSurfacedAndBlocked()
    {
        using var h = new ReviewHarness();
        h.Source.Items.Add(ReviewHarness.Row(1, 1600m) with { DueDate = new DateTime(2026, 9, 10), VoucherNumber = "INV-SEP", PlanAmount = 1600m });
        h.Source.Items.Add(ReviewHarness.Row(1, 400m, day: 20));
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, h.All(), CancellationToken.None)).Value!;
        Assert.Equal(0, summary.Count);
        var group = Assert.Single((await scope.Query.OverlapsAsync(h.Manager, 1, 25, CancellationToken.None)).Value!.Groups);
        Assert.True(group.ReminderTypeOverlap);
        Assert.False(group.SharedAcrossUnits);
        Assert.Equal(1, group.UnitCount);
        Assert.Equal(["CurrentMonth", "LegalNotice"], group.ReminderTypes);
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Query.OverlapsAsync(h.Reporting, 1, 25, CancellationToken.None)).Outcome);
    }

    // ------------------------------------------------------------ happy path & Genesys mapping

    [Fact]
    public async Task ApprovedList_IsRevalidatedAndUploaded_WithTheExactGenesysMapping()
    {
        using var h = await SeededAsync(2);
        var (summary, confirm) = await h.PrepareAsync();
        var accepted = await ConfirmAsync(h, confirm);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        Assert.Equal(h.Manager.EmployeeId, accepted.Value!.InitiatedByEmployeeId);
        Assert.Equal(2, accepted.Value.ApprovedCount);
        Assert.Equal(summary.TotalsByCurrency["AED"], accepted.Value.ApprovedTotalsByCurrency["AED"]);
        Assert.Single(h.Scheduler.Dispatches);                // exactly one job queued
        Assert.Empty(h.Client.Calls);                          // confirming uploads nothing: the job does

        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);

        var call = Assert.Single(h.Client.Calls);
        Assert.Equal("79e5ae74-ea6e-4941-b76d-45ddf487d8d1", call.ContactListId);        // Current Month list
        Assert.Equal(2, call.Contacts.Count);
        var first = call.Contacts.Single(c => c.CustomerName == "Customer 1");
        Assert.Equal(call.ContactListId, first.ContactListId);                            // URL list id == body list id
        Assert.Equal("+971500000001", first.Phone);
        Assert.Equal("c1@example.test", first.EmailAddress);
        Assert.Equal("Current Month", first.ReminderType);
        Assert.Equal("101.00", first.AmountDue);
        Assert.Equal("2026-10-20", first.DueDate);
        Assert.True(first.Callable);

        var done = (await scope.Dispatch.GetAsync(h.Manager, accepted.Value.DispatchId, CancellationToken.None)).Value!;
        Assert.Equal("Completed", done.Status);
        Assert.Equal(2, done.UploadedCount);
        var batch = Assert.Single(done.Batches);
        Assert.Equal("Uploaded", batch.Status);
        Assert.Equal(2, batch.ReturnedContactCount);
        Assert.Equal(1, batch.AttemptCount);
        Assert.NotNull(batch.StartedAtUtc);
        Assert.NotNull(batch.CompletedAtUtc);
        var items = await ItemsAsync(h);
        Assert.All(items, i => { Assert.Equal(DispatchItemStatus.UploadedToGenesys, i.Status); Assert.StartsWith("contact-", i.GenesysContactId); Assert.NotNull(i.UploadedAtUtc); });
        // "Uploaded" is not "contacted": the vocabulary and the disclaimer keep them apart.
        Assert.DoesNotContain(Enum.GetNames<DispatchItemStatus>(), n => n.Contains("Contacted") || n.Contains("Delivered") || n.Contains("Called"));
        Assert.Contains("does not mean the customer was called", done.Disclaimer);
    }

    [Fact]
    public async Task EachReminderTypeGoesToItsOwnContactList_InSeparateRequests()
    {
        using var h = new ReviewHarness();
        h.Source.Items.Add(ReviewHarness.Row(1));                                                                  // Current Month
        h.Source.Items.Add(ReviewHarness.Row(2, 1600m) with { DueDate = new DateTime(2026, 9, 10), PlanAmount = 1600m });  // Legal Notice (and an out-of-schedule Overdue)
        await h.RefreshAsync();
        var dispatch = await ApproveAndRunAsync(h, selection: h.All());
        Assert.Equal("Completed", dispatch.Status);
        Assert.Equal(2, h.Client.Calls.Count);
        var byList = h.Client.Calls.ToDictionary(c => c.ContactListId, c => c.Contacts.Single());
        Assert.Equal("Current Month", byList["79e5ae74-ea6e-4941-b76d-45ddf487d8d1"].ReminderType);
        Assert.Equal("Legal Notice", byList["372d81d7-6d2d-4b9e-8f09-cf2262aecfdf"].ReminderType);
        Assert.Equal("1600.00", byList["372d81d7-6d2d-4b9e-8f09-cf2262aecfdf"].AmountDue);
    }

    [Fact]
    public async Task ABatchNeverExceeds1000Contacts_AndNeverMixesReminderTypes()
    {
        using var h = new ReviewHarness();
        for (var n = 1; n <= 2500; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n % 50));
        h.Source.Items.Add(ReviewHarness.Row(9001, 1600m) with { DueDate = new DateTime(2026, 9, 10), PlanAmount = 1600m });
        await h.RefreshAsync();
        h.Genesys.BatchSize = 50_000;                                     // misconfiguration is clamped to the Genesys limit
        var dispatch = await ApproveAndRunAsync(h, selection: h.All());
        Assert.Equal("Completed", dispatch.Status);
        Assert.Equal(2501, dispatch.ApprovedCount);
        var cm = h.Client.Calls.Where(c => c.ContactListId == "79e5ae74-ea6e-4941-b76d-45ddf487d8d1").ToList();
        Assert.Equal(new[] { 1000, 1000, 500 }, cm.Select(c => c.Contacts.Count));
        Assert.Single(h.Client.Calls, c => c.ContactListId == "372d81d7-6d2d-4b9e-8f09-cf2262aecfdf");
        Assert.All(h.Client.Calls, c => Assert.True(c.Contacts.Count <= 1000));
        Assert.All(h.Client.Calls, c => Assert.Single(c.Contacts.Select(x => x.ReminderType).Distinct()));
        Assert.Equal(2501, h.Client.Calls.Sum(c => c.Contacts.Count));
        Assert.Equal(2501, h.Client.Calls.SelectMany(c => c.Contacts).Select(c => c.Phone).Distinct().Count());   // no contact sent twice
        Assert.Equal(new[] { 1, 2, 3, 4 }, dispatch.Batches.Select(b => b.Sequence));
    }

    // ------------------------------------------------------------ duplicate prevention

    [Fact]
    public async Task DoubleClick_WithTheSameKey_ReturnsTheOriginalDispatch()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(key: "double-click");
        var first = await ConfirmAsync(h, confirm);
        var second = await ConfirmAsync(h, confirm);
        Assert.Equal(CollectionsOutcome.Accepted, first.Outcome);
        Assert.Equal(CollectionsOutcome.Replayed, second.Outcome);
        Assert.Equal(first.Value!.DispatchId, second.Value!.DispatchId);
        Assert.Single(h.Scheduler.Dispatches);
        using var context = h.CreateContext();
        Assert.Equal(1, await context.CollectionsDispatches.CountAsync());
        Assert.Equal(3, await context.CollectionsDispatchItems.CountAsync());
    }

    [Fact]
    public async Task ReusingAKeyForADifferentApprovalOrUserIsAConflict()
    {
        using var h = await SeededAsync(4);
        var (_, confirm) = await h.PrepareAsync(key: "shared-key");
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, confirm)).Outcome);
        var other = new CollectionsCaller(Guid.NewGuid(), h.Manager.Roles, []);
        Assert.Equal(CollectionsOutcome.IdempotencyConflict, (await ConfirmAsync(h, confirm, other)).Outcome);
    }

    [Fact]
    public async Task ApprovedRecordsShowAsAlreadySent_AndCannotBeApprovedAgainWithAnotherKey()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(key: "first");
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, confirm)).Outcome);
        var again = await ConfirmAsync(h, confirm with { IdempotencyKey = "second" });
        Assert.False(again.IsSuccess);
        Assert.Single(h.Scheduler.Dispatches);

        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", ValidationStatus: "AlreadySent"), 1, 25, CancellationToken.None)).Value!;
        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, i => { Assert.Equal("Approved", i.PreviousDispatchStatus); Assert.NotNull(i.PreviousDispatchAtUtc); });
        Assert.Equal(3, page.Counts.AlreadySent);
        Assert.Equal(0, page.Counts.Ready);
    }

    [Fact]
    public async Task ConcurrentConfirmations_OfTheSameRecords_CreateExactlyOneDispatch()
    {
        using var h = await SeededAsync(40, file: true);
        var (_, confirm) = await h.PrepareAsync();
        var attempts = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            using var scope = h.NewScope();
            try { return await scope.Dispatch.ConfirmAsync(h.Manager, confirm with { IdempotencyKey = $"race-{i}" }, CancellationToken.None); }
            catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException) { return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.DuplicateDispatch); }
        })).ToArray();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(r => r.Outcome == CollectionsOutcome.Accepted));
        using var context = h.CreateContext();
        Assert.Equal(1, await context.CollectionsDispatches.CountAsync());
        Assert.Equal(40, await context.CollectionsDispatchItems.CountAsync());
        Assert.Equal(40, await context.CollectionsDispatchItems.Select(i => i.RecordKey).Distinct().CountAsync());
    }

    [Fact]
    public async Task DuplicateJobExecution_UploadsEachContactOnce()
    {
        using var h = await SeededAsync(30, file: true);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        var id = h.Scheduler.Dispatches.Single();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var scope = h.NewScope();
            await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        })));
        using (var scope = h.NewScope()) await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);   // and once more afterwards
        Assert.Equal(30, h.Client.Calls.Sum(c => c.Contacts.Count));
        Assert.Equal(30, h.Client.Calls.SelectMany(c => c.Contacts).Select(c => c.Phone).Distinct().Count());
        Assert.Single(h.Client.Calls);
    }

    [Fact]
    public async Task ADispatchLeaseAdmitsOneWorkerUntilItExpires()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        var id = h.Scheduler.Dispatches.Single();
        using var scope = h.NewScope();
        var now = ReviewHarness.Now;
        Assert.True(await scope.Store.TryAcquireLeaseAsync(id, Guid.NewGuid(), now, TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.False(await scope.Store.TryAcquireLeaseAsync(id, Guid.NewGuid(), now.AddMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.True(await scope.Store.TryAcquireLeaseAsync(id, Guid.NewGuid(), now.AddMinutes(6), TimeSpan.FromMinutes(5), CancellationToken.None)); // the first worker died
    }

    // ------------------------------------------------------------ changes between review and dispatch

    [Fact]
    public async Task APaidRecordIsExcludedAtDispatch_AndTheRestStillGo()
    {
        using var h = await SeededAsync(3);
        var (_, confirm) = await h.PrepareAsync();
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, confirm)).Outcome);
        h.Source.Items.RemoveAll(r => r.TenantId == "T0002");          // paid in PACT after approval
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);

        var sent = Assert.Single(h.Client.Calls).Contacts.Select(c => c.CustomerName).Order().ToArray();
        Assert.Equal(["Customer 1", "Customer 3"], sent);
        var items = await ItemsAsync(h);
        var paid = Assert.Single(items, i => i.Status == DispatchItemStatus.Excluded);
        Assert.Equal("Customer 2", paid.CustomerName);
        Assert.Contains("Paid or settled", paid.StatusReason);
        Assert.Null(paid.GenesysContactId);
        var dispatch = (await scope.Dispatch.GetAsync(h.Manager, (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!.PublicId, CancellationToken.None)).Value!;
        Assert.Equal("Completed", dispatch.Status);
        Assert.Equal(1, dispatch.ExcludedCount);
        Assert.Equal(2, dispatch.UploadedCount);
    }

    [Fact]
    public async Task AChangedBalanceStopsTheWholeDispatchForReview_AndNothingIsUploaded()
    {
        using var h = await SeededAsync(3);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        var index = h.Source.Items.FindIndex(r => r.TenantId == "T0001");
        h.Source.Items[index] = h.Source.Items[index] with { Amount = 40m };          // partly paid after approval
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);

        Assert.Empty(h.Client.Calls);
        var dispatch = (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!;
        Assert.Equal(DispatchStatus.ReviewRequired, dispatch.Status);
        Assert.Contains("review the current list", dispatch.StatusReason);
        Assert.All(dispatch.Items, i => Assert.Equal(DispatchItemStatus.Released, i.Status));
        // The records are free again and the next review shows the new balance.
        await h.RefreshAsync();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", MinRemaining: 0m), 1, 25, CancellationToken.None)).Value!;
        Assert.Equal(0, page.Counts.AlreadySent);
        Assert.Equal(40m, page.Items.Single(i => i.CustomerName == "Customer 1").RemainingAmount);
    }

    [Fact]
    public async Task AChangedPhoneOrDueDateAlsoRequiresReview()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        var index = h.Source.Items.FindIndex(r => r.TenantId == "T0002");
        h.Source.Items[index] = h.Source.Items[index] with { Mobile = "0559998888" };
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        Assert.Equal(DispatchStatus.ReviewRequired, (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task IfCurrentBalancesCannotBeRead_NothingIsSent()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        h.Source.Failure = new PactReceivablesSourceException("The PACT report connection is not configured.");
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        var dispatch = (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!;
        Assert.Equal(DispatchStatus.Failed, dispatch.Status);
        Assert.All(dispatch.Items, i => Assert.Equal(DispatchItemStatus.Released, i.Status));
    }

    [Fact]
    public async Task EveryApprovedRecordPaidBeforeSending_UploadsNothing()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        h.Source.Items.Clear();
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        Assert.Equal(DispatchStatus.Completed, (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!.Status);
        Assert.All(await ItemsAsync(h), i => Assert.Equal(DispatchItemStatus.Excluded, i.Status));
    }

    [Fact]
    public async Task ChangesBetweenReviewAndConfirmationRequireReviewAgain()
    {
        using var h = await SeededAsync(3);
        var (_, confirm) = await h.PrepareAsync();
        var index = h.Source.Items.FindIndex(r => r.TenantId == "T0003");
        h.Source.Items[index] = h.Source.Items[index] with { Amount = 90m, PlanAmount = 103m };   // balance changed, then refreshed
        await h.RefreshAsync();
        var stale = await ConfirmAsync(h, confirm);
        Assert.Equal(CollectionsOutcome.ReviewRequired, stale.Outcome);
        Assert.Contains("changed since this list was shown", stale.Detail);
        Assert.Empty(await ItemsAsync(h));
        // Confirming the freshly shown list works.
        var (_, fresh) = await h.PrepareAsync(key: "fresh");
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, fresh)).Outcome);
    }

    [Fact]
    public async Task ARefreshThatChangesNothingDoesNotInvalidateTheApproval()
    {
        using var h = await SeededAsync(3);
        var (_, confirm) = await h.PrepareAsync();
        await h.RefreshAsync();
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, confirm)).Outcome);
    }

    [Fact]
    public async Task StaleReviewDataCannotBeApproved()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        h.Time.Advance(TimeSpan.FromMinutes(90));
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, h.All(new ReviewFilter(ReminderType: "CurrentMonth")), CancellationToken.None)).Value!;
        Assert.Equal(0, summary.Count);          // stale records are not Ready, so nothing can be selected
        Assert.False((await ConfirmAsync(h, confirm)).IsSuccess);
        Assert.Empty(await ItemsAsync(h));
    }

    // ------------------------------------------------------------ partial failure & unknown outcomes

    [Fact]
    public async Task APartialFailure_KeepsTheGoodBatches_AndFailsOnlyTheRejectedOne()
    {
        using var h = new ReviewHarness();
        for (var n = 1; n <= 2500; n++) h.Source.Items.Add(ReviewHarness.Row(n));
        await h.RefreshAsync();
        h.Client.Script.Enqueue(GenesysUploadOutcome.Accepted);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Rejected);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Accepted);
        var dispatch = await ApproveAndRunAsync(h, selection: h.All(new ReviewFilter(ReminderType: "CurrentMonth")));

        Assert.Equal("CompletedWithErrors", dispatch.Status);
        Assert.Equal(new[] { "Uploaded", "Failed", "Uploaded" }, dispatch.Batches.Select(b => b.Status));
        Assert.Equal(1500, dispatch.UploadedCount);
        Assert.Equal(1000, dispatch.FailedCount);
        Assert.Equal(0, dispatch.UnknownCount);
        Assert.Equal(400, dispatch.Batches[1].HttpStatus);
        Assert.Equal(3, h.Client.Calls.Count);

        // The rejected records were not delivered anywhere and become approvable again.
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var counts = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth"), 1, 25, CancellationToken.None)).Value!.Counts;
        Assert.Equal(1000, counts.Ready);
        Assert.Equal(1500, counts.AlreadySent);
    }

    [Fact]
    public async Task ATimeout_IsAnUnknownOutcome_NeverResent_AndBlocksDuplicates()
    {
        using var h = await SeededAsync(3);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Unknown);
        var dispatch = await ApproveAndRunAsync(h);

        Assert.Equal("CompletedWithErrors", dispatch.Status);
        var batch = Assert.Single(dispatch.Batches);
        Assert.Equal("UnknownOutcome", batch.Status);
        Assert.Equal(3, dispatch.UnknownCount);
        Assert.Equal(0, dispatch.UploadedCount);
        Assert.Contains("timed out", batch.Error);

        // A retried/duplicated job must not resend it.
        using (var scope = h.NewScope()) await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Single(h.Client.Calls);

        // The contacts may exist in Genesys, so the records stay blocked and cannot be approved again.
        await h.RefreshAsync();
        using var query = h.NewScope();
        var counts = (await query.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth"), 1, 25, CancellationToken.None)).Value!.Counts;
        Assert.Equal(3, counts.AlreadySent);
        Assert.Equal(0, counts.Ready);
    }

    [Fact]
    public async Task AnExceptionFromTheClient_IsTreatedAsAnUnknownOutcome()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        using var scope = h.NewScope(new ThrowingClient(new TimeoutException()));
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        var dispatch = (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!;
        Assert.Equal(GenesysBatchStatus.UnknownOutcome, Assert.Single(dispatch.Batches).Status);
        Assert.All(dispatch.Items, i => Assert.Equal(DispatchItemStatus.UnknownOutcome, i.Status));
    }

    private sealed class ThrowingClient(Exception failure) : IGenesysOutboundClient
    {
        // The destination check before an upload is read-only and succeeds here; this double only misbehaves where its name says.
        public Task<GenesysContactListInfo> GetContactListAsync(string contactListId, CancellationToken ct) => Task.FromResult(new GenesysContactListInfo(GenesysUploadOutcome.Accepted, 200, TigerCS.Application.Modules.Collections.Review.GenesysContactTemplate.Columns.ToList(), null));
        public Task<GenesysCampaignLookupResult> FindCampaignsByContactListAsync(string contactListId, CancellationToken ct) => Task.FromResult(new GenesysCampaignLookupResult(GenesysUploadOutcome.Accepted, 200, [], null));
        public Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct) => throw failure;
        public Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct) => throw failure;
        public Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct) => throw failure;
        public Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string contactListId, GenesysCampaignOptions template, CancellationToken ct) => throw failure;
    }

    [Fact]
    public async Task AWorkerThatDiedMidRequest_LeavesAnUnknownOutcome_NotARetry()
    {
        using var h = await SeededAsync(2);
        var dispatch = await ApproveAndRunAsync(h);
        Assert.Equal("Completed", dispatch.Status);
        h.Client.Calls.Clear();
        // Simulate the crash: the batch was claimed (Submitting) but the answer was never recorded.
        using (var context = h.CreateContext())
        {
            var batch = await context.CollectionsGenesysBatches.SingleAsync();
            batch.Status = GenesysBatchStatus.Submitting; batch.Error = null;
            var row = await context.CollectionsDispatches.SingleAsync();
            row.Status = DispatchStatus.Sending; row.LeaseOwner = Guid.NewGuid(); row.LeaseExpiresAtUtc = ReviewHarness.Now.AddMinutes(-1);
            foreach (var item in context.CollectionsDispatchItems) { item.Status = DispatchItemStatus.Approved; item.GenesysContactId = null; }
            await context.SaveChangesAsync();
        }
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);                                   // not resent
        var after = (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!;
        Assert.Equal(GenesysBatchStatus.UnknownOutcome, Assert.Single(after.Batches).Status);
        Assert.All(after.Items, i => Assert.Equal(DispatchItemStatus.UnknownOutcome, i.Status));
        Assert.Equal(DispatchStatus.CompletedWithErrors, after.Status);
    }

    [Fact]
    public async Task Reconciliation_ConfirmedNotUploaded_ReleasesTheRecords()
    {
        using var h = await SeededAsync(3);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Unknown);
        var dispatch = await ApproveAndRunAsync(h);
        using var scope = h.NewScope();
        var batchId = dispatch.Batches.Single().BatchId;

        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Dispatch.ReconcileAsync(h.Manager, dispatch.DispatchId, batchId, new("ConfirmedNotUploaded", " "), CancellationToken.None)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Dispatch.ReconcileAsync(h.Manager, dispatch.DispatchId, batchId, new("Maybe", "checked"), CancellationToken.None)).Outcome);
        var done = await scope.Dispatch.ReconcileAsync(h.Manager, dispatch.DispatchId, batchId, new("ConfirmedNotUploaded", "List export shows no new contacts"), CancellationToken.None);
        Assert.True(done.IsSuccess, done.Detail);
        var batch = done.Value!.Batches.Single();
        Assert.Equal("ConfirmedNotUploaded", batch.Status);
        Assert.NotNull(batch.ReconciledAtUtc);
        Assert.Equal("List export shows no new contacts", batch.ReconciliationNote);
        Assert.All(await ItemsAsync(h), i => Assert.Equal(DispatchItemStatus.Released, i.Status));

        // Only an unknown outcome can be reconciled, and only once.
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Dispatch.ReconcileAsync(h.Manager, dispatch.DispatchId, batchId, new("ConfirmedUploaded", "again"), CancellationToken.None)).Outcome);

        // The records can be reviewed and approved again, with fresh validation.
        await h.RefreshAsync();
        var (summary, confirm) = await h.PrepareAsync(key: "retry");
        Assert.Equal(3, summary.Count);
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, confirm)).Outcome);
    }

    [Fact]
    public async Task Reconciliation_ConfirmedUploaded_KeepsTheRecordsBlocked()
    {
        using var h = await SeededAsync(2);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Unknown);
        var dispatch = await ApproveAndRunAsync(h);
        using var scope = h.NewScope();
        var done = await scope.Dispatch.ReconcileAsync(h.Manager, dispatch.DispatchId, dispatch.Batches.Single().BatchId, new("ConfirmedUploaded", "2 contacts found in the list"), CancellationToken.None);
        Assert.Equal(2, done.Value!.UploadedCount);
        Assert.Equal(h.Manager.EmployeeId, (await scope.Store.GetBatchAsync(dispatch.Batches.Single().BatchId, CancellationToken.None))!.ReconciledByEmployeeId);
        await h.RefreshAsync();
        var counts = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth"), 1, 25, CancellationToken.None)).Value!.Counts;
        Assert.Equal(2, counts.AlreadySent);
    }

    [Fact]
    public async Task AnApprovalFromAnEarlierBusinessMonthRequiresReview()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        h.Time.Advance(TimeSpan.FromDays(20));          // the next month
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        var dispatch = (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!;
        Assert.Equal(DispatchStatus.ReviewRequired, dispatch.Status);
        Assert.Contains("month changed", dispatch.StatusReason);
    }

    [Fact]
    public async Task ADispatchIsNotSentIfTheIntegrationWasDisabledAfterApproval()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        await ConfirmAsync(h, confirm);
        h.Genesys.Enabled = false;
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        Assert.Equal(0, h.Source.Reads - 1);
        Assert.Equal(DispatchStatus.Failed, (await scope.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task AQueuedDispatchCanBeCancelled_AndItsRecordsAreReleased_ButARunningOneCannot()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        var queued = (await ConfirmAsync(h, confirm)).Value!;
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Dispatch.CancelAsync(h.Agent, queued.DispatchId, CancellationToken.None)).Outcome);
        var cancelled = await scope.Dispatch.CancelAsync(h.Manager, queued.DispatchId, CancellationToken.None);
        Assert.Equal("Cancelled", cancelled.Value!.Status);
        Assert.All(await ItemsAsync(h), i => Assert.Equal(DispatchItemStatus.Released, i.Status));
        // A cancelled dispatch never uploads, even if its job runs later.
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Dispatch.CancelAsync(h.Manager, queued.DispatchId, CancellationToken.None)).Outcome);
        // The records are free to be approved again.
        var (_, again) = await h.PrepareAsync(key: "again");
        Assert.Equal(CollectionsOutcome.Accepted, (await ConfirmAsync(h, again)).Outcome);
    }
}
