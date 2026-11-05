using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Direct Send: a user-controlled contact-list dispatch with no campaign stage and no stage dates. It waives ONLY the stage schedule. Reconciliation, permissions, contact and identity validation,
/// shared-phone, legal-stage, duplicate-send and contact-frequency rules stay in force, and nothing is ever dispatched without an explicit confirmation. The harness clock starts on 14 Oct 2026
/// (Current Month is scheduled); most tests advance to 15 Oct, a day on which no stage is scheduled.
/// </summary>
public sealed class DirectSendReviewTests
{
    private const string DirectList = "6d2b9a0e-5c1f-4c7e-9a43-1b8f0e2d7a55";
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null, bool advanceDay = true, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        if (advanceDay) { h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1); }
        h.Genesys.ContactListIds["DirectSend"] = DirectList;
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : [ReviewHarness.Row(1), ReviewHarness.Row(2)]);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter DirectFilter(Func<ReviewFilter, ReviewFilter>? with = null)
    {
        var filter = new ReviewFilter(ReminderType: "DirectSend", SendingMode: "DirectSend");
        return with is null ? filter : with(filter);
    }

    private static SelectionRequest DirectAll(ReviewFilter? filter = null) => new(filter ?? DirectFilter(), ReviewQueryService.ModeAllMatching);

    private static async Task<CollectionsResult<SelectionSummaryDto>> SummaryAsync(ReviewHarness h, SelectionRequest request, CollectionsCaller? caller = null)
    {
        using var scope = h.NewScope();
        return await scope.Query.SummarizeAsync(caller ?? h.Manager, request, CancellationToken.None);
    }

    private static async Task<ReviewPageDto> ListAsync(ReviewHarness h, ReviewFilter filter)
    {
        using var scope = h.NewScope();
        var result = await scope.Query.QueryAsync(h.Manager, filter, 1, 100, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Detail);
        return result.Value!;
    }

    private static ReviewBuildContext Context(DateOnly asOf) => new(asOf, ReviewHarness.Now, "PACT test", "AED", true, true, false,
        MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>());

    // ------------------------------------------------------------ the mode itself

    [Fact]
    public void TheThreeSendingModes_Parse_AndAnythingElseIsRefused()
    {
        foreach (var (text, expected) in new[] { ("", "Auto"), ("auto", "Auto"), ("Manual", "Manual"), ("DirectSend", "DirectSend"), ("direct send".Replace(" ", ""), "DirectSend"), ("Direct", "DirectSend") })
        {
            Assert.True(CollectionsSendingModes.TryParse(text, out var mode)); Assert.Equal(expected, mode);
        }
        Assert.False(CollectionsSendingModes.TryParse("Instant", out _));
        Assert.True(CollectionsSendingModes.IsDirectSend("DirectSend")); Assert.False(CollectionsSendingModes.IsDirectSend(null)); Assert.False(CollectionsSendingModes.IsDirectSend("Manual"));
        Assert.True(CollectionsSendingModes.IsManual("Manual")); Assert.False(CollectionsSendingModes.IsManual("DirectSend"));
    }

    // ------------------------------------------------------------ builder: one record per customer, no stage schedule

    [Fact]
    public void DirectSend_IsOneRecordPerCustomer_ForTheWholeBalance_WithoutAnyStageDate()
    {
        var rows = new[] { ReviewHarness.Row(1, 400m, day: 20), ReviewHarness.Row(1, 250m, day: 25) with { VoucherNumber = "INV-1B" } };   // two instalments of one unit, both this month
        var records = ReviewRecordBuilder.Build(rows, Context(Today)).Records;
        var direct = Assert.Single(records, r => r.ReminderType == CampaignReminderType.DirectSend);
        Assert.Equal(ReviewValidationStatus.Ready, direct.ValidationStatus);
        Assert.Equal(650m, direct.RemainingAmount);                                              // Due + Overdue of the customer, not a stage amount
        Assert.Equal(new DateOnly(2026, 10, 20), direct.DueDate);
        Assert.DoesNotContain(ReviewReasons.OutsideSchedule, ReviewRecordBuilder.Parse(direct.Reasons));
        Assert.EndsWith(":DirectSend", direct.CycleKey);
        // The stage records are exactly what they were: still outside their schedule today, and no Direct Send marker leaks into them.
        var current = Assert.Single(records, r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal([ReviewReasons.OutsideSchedule], ReviewRecordBuilder.Parse(current.Reasons));
    }

    [Fact]
    public void DirectSend_DoesNotDisturbTheRepeatedCallRulesOfTheStages()
    {
        // 14 Oct: Current Month is scheduled. The unit's stage record stays Ready (the Direct Send record is not an "overlapping reminder type"), and the Direct Send record is held back.
        var records = ReviewRecordBuilder.Build([ReviewHarness.Row(1)], Context(new DateOnly(2026, 10, 14))).Records;
        var current = Assert.Single(records, r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal(ReviewValidationStatus.Ready, current.ValidationStatus);
        Assert.Empty(ReviewRecordBuilder.Parse(current.Reasons));
        var direct = Assert.Single(records, r => r.ReminderType == CampaignReminderType.DirectSend);
        Assert.Equal(ReviewValidationStatus.Excluded, direct.ValidationStatus);
        Assert.Equal([ReviewReasons.DirectSendScheduledToday], ReviewRecordBuilder.Parse(direct.Reasons));
    }

    [Fact]
    public void DirectSend_HoldsBackSharedPhones_LegalStageCustomers_AndBadContacts()
    {
        var shared = ReviewRecordBuilder.Build([ReviewHarness.Row(3, mobile: "971500000001"), ReviewHarness.Row(4, mobile: "971500000001"), ReviewHarness.Row(5)], Context(Today)).Records
            .Where(r => r.ReminderType == CampaignReminderType.DirectSend).OrderBy(r => r.UnitCode).ToList();
        Assert.Equal(ReviewValidationStatus.Excluded, shared[0].ValidationStatus); Assert.Contains(ReviewReasons.SharedPhoneMultipleUnits, ReviewRecordBuilder.Parse(shared[0].Reasons));
        Assert.Equal(ReviewValidationStatus.Excluded, shared[1].ValidationStatus);
        Assert.Equal(ReviewValidationStatus.Ready, shared[2].ValidationStatus);

        // A balance in a legal stage (last month, above the legal-notice threshold) is never sent through a generic reminder.
        var legal = ReviewHarness.Row(6, 2000m) with { DueDate = new DateTime(2026, 9, 10) };
        var record = Assert.Single(ReviewRecordBuilder.Build([legal], Context(Today)).Records, r => r.ReminderType == CampaignReminderType.DirectSend);
        Assert.Equal(ReviewValidationStatus.Excluded, record.ValidationStatus);
        Assert.Contains(ReviewReasons.LegalStageCustomer, ReviewRecordBuilder.Parse(record.Reasons));

        var noPhone = Assert.Single(ReviewRecordBuilder.Build([ReviewHarness.Row(7, mobile: "123")], Context(Today)).Records, r => r.ReminderType == CampaignReminderType.DirectSend);
        Assert.Equal(ReviewValidationStatus.NeedsReview, noPhone.ValidationStatus); Assert.Contains(ReviewReasons.NoValidContact, ReviewRecordBuilder.Parse(noPhone.Reasons));

        var unreconciled = ReviewRecordBuilder.Build([ReviewHarness.Row(8)], Context(Today) with { SourceReconciled = false }).Records.Single(r => r.ReminderType == CampaignReminderType.DirectSend);
        Assert.Equal(ReviewValidationStatus.NeedsReview, unreconciled.ValidationStatus); Assert.Contains(ReviewReasons.SourceReconciliationRequired, ReviewRecordBuilder.Parse(unreconciled.Reasons));
    }

    // ------------------------------------------------------------ listing and selection

    [Fact]
    public async Task DirectSend_ListsEligibleCustomersWithoutAStageOrASendDate_AndNeverSendsAnything()
    {
        using var h = await SeededAsync();
        var page = await ListAsync(h, DirectFilter());
        Assert.Equal(2, page.Counts.Ready); Assert.All(page.Items, i => { Assert.Equal("DirectSend", i.ReminderType); Assert.Equal("Ready", i.ValidationStatus); Assert.NotNull(i.TenantId); });
        var summary = (await SummaryAsync(h, DirectAll())).Value!;
        Assert.Equal(2, summary.Count); Assert.Equal(1000m, summary.TotalsByCurrency["AED"]);
        Assert.Equal(DirectList, summary.ContactListByReminderType!["DirectSend"]);
        Assert.Empty(h.Scheduler.Dispatches);                                                   // refresh, listing and summary queue nothing: no automatic dispatch
        Assert.Empty(h.Client.Calls);
    }

    [Fact]
    public async Task DirectSend_FiltersByTower_PaymentStatus_DateRange_AndMinimumAmount()
    {
        var rows = new[]
        {
            ReviewHarness.Row(1, 900m, day: 20), ReviewHarness.Row(2, 150m, day: 20), ReviewHarness.Row(3, 500m, day: 20) with { DueDate = new DateTime(2026, 8, 10) },
            ReviewHarness.Row(4, 700m, day: 20) with { TowerId = 7, TowerNumber = "124", TowerName = "Tower 124" }
        };
        using var h = await SeededAsync(null, true, rows);
        Assert.Equal(4, (await ListAsync(h, DirectFilter())).Counts.Total);
        Assert.Equal(3, (await ListAsync(h, DirectFilter(f => f with { MinRemaining = 200m }))).Counts.Total);                       // Minimum amount
        var due = await ListAsync(h, DirectFilter(f => f with { DueStatus = "Due" }));                                               // Payment status Due: the balance falls inside this month
        Assert.DoesNotContain(due.Items, i => i.UnitCode == "TP140-3");
        var overdue = await ListAsync(h, DirectFilter(f => f with { DueStatus = "Overdue" }));                                       // Overdue: there is a balance before this month
        Assert.Equal(["TP140-3"], overdue.Items.Select(i => i.UnitCode));
        var range = await ListAsync(h, DirectFilter(f => f with { DueFrom = new DateOnly(2026, 8, 1), DueTo = new DateOnly(2026, 8, 31) }));   // Date range
        Assert.Equal(["TP140-3"], range.Items.Select(i => i.UnitCode));
        var tower = await ListAsync(h, DirectFilter(f => f with { TowerId = 7 }));                                                   // Tower
        Assert.Equal(["TP140-4"], tower.Items.Select(i => i.UnitCode));
    }

    [Fact]
    public async Task SelectedCustomers_AreExactlyWhatIsResolved_AndSelectAllCoversEveryEligibleCustomer()
    {
        using var h = await SeededAsync(null, true, ReviewHarness.Row(1), ReviewHarness.Row(2), ReviewHarness.Row(3));
        var page = await ListAsync(h, DirectFilter());
        var first = page.Items[0];
        var picked = new SelectionRequest(DirectFilter(), ReviewQueryService.ModeSelectedUnits, null, null, 1, 50, [ReviewQueryService.UnitKey(first.CompanyId, first.TenantId!, first.UnitCode)]);
        var one = (await SummaryAsync(h, picked)).Value!;
        Assert.Equal(1, one.Count); Assert.Equal(first.UnitCode, one.Contacts.Single().UnitCode);
        var excluding = DirectAll() with { ExcludedKeys = [first.RecordKey] };
        Assert.Equal(2, (await SummaryAsync(h, excluding)).Value!.Count);
        Assert.Equal(3, (await SummaryAsync(h, DirectAll())).Value!.Count);                                                          // Select All eligible
    }

    // ------------------------------------------------------------ the mode and the reminder type must agree; stages never see Direct Send

    [Fact]
    public async Task DirectSend_IsNeverMixedWithTheStages_AndTheStagesNeverSeeIt()
    {
        using var h = await SeededAsync();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await SummaryAsync(h, DirectAll(DirectFilter(f => f with { SendingMode = "Auto" })))).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await SummaryAsync(h, DirectAll(DirectFilter(f => f with { SendingMode = "Manual", SendDate = Today })))).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await SummaryAsync(h, DirectAll(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "DirectSend")))).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await SummaryAsync(h, DirectAll(new ReviewFilter(SendingMode: "DirectSend")))).Outcome);
        // No reminder type = every campaign stage, never Direct Send: the listing, the counts and an "all matching" approval ignore it.
        var stages = await ListAsync(h, new ReviewFilter());
        Assert.DoesNotContain(stages.Items, i => i.ReminderType == "DirectSend"); Assert.Equal(4, stages.Counts.Total);          // 2 units x the 2 stages whose window holds this month's instalment
        Assert.Equal(0, (await SummaryAsync(h, h.All())).Value!.Count);                                                               // 15 Oct: nothing is scheduled
        // Explicit keys cannot reach a Direct Send record either.
        var keys = (await ListAsync(h, DirectFilter())).Items.Select(i => i.RecordKey).ToList();
        Assert.Equal(2, keys.Count);
        Assert.Equal(0, (await SummaryAsync(h, new SelectionRequest(null, ReviewQueryService.ModeSelectedKeys, keys))).Value!.Count);
    }

    [Fact]
    public async Task Manual_AndAuto_BehaveAsBefore_WhileDirectSendRecordsExist()
    {
        using var h = await SeededAsync();
        // Auto outside the schedule: nothing to approve. Manual on the send date: both customers. Exactly as without Direct Send.
        Assert.Equal(0, (await SummaryAsync(h, new(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Auto"), ReviewQueryService.ModeAllMatching))).Value!.Count);
        var manual = (await SummaryAsync(h, new(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", SendDate: Today), ReviewQueryService.ModeAllMatching))).Value!;
        Assert.Equal(2, manual.Count); Assert.Equal(["CurrentMonth"], manual.CountByReminderType.Keys);
        // On a scheduled day Auto still approves the stage records, with no Direct Send in the list.
        using var scheduled = await SeededAsync(null, false);
        var auto = (await SummaryAsync(scheduled, scheduled.All(new ReviewFilter(ReminderType: "CurrentMonth")))).Value!;
        Assert.Equal(2, auto.Count); Assert.Equal(["CurrentMonth"], auto.CountByReminderType.Keys);
        // And the stage's contact list is untouched by the Direct Send list.
        Assert.NotEqual(DirectList, auto.ContactListByReminderType!["CurrentMonth"]);
    }

    // ------------------------------------------------------------ safety controls

    [Fact]
    public async Task DirectSend_DoesNotBypassReconciliation_Permissions_OrFreshData()
    {
        using var unreconciled = await SeededAsync(h => h.Campaign.FinancialSourceValidated = false);
        Assert.Equal(0, (await SummaryAsync(unreconciled, DirectAll())).Value!.Count);
        var held = await ListAsync(unreconciled, DirectFilter());
        Assert.All(held.Items, i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.SourceReconciliationRequired));

        using var ok = await SeededAsync();
        Assert.Equal(CollectionsOutcome.Forbidden, (await SummaryAsync(ok, DirectAll(), ok.Agent)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await SummaryAsync(ok, DirectAll(), ok.Reporting)).Outcome);

        ok.Time.Advance(TimeSpan.FromDays(1));                                                  // 16 Oct: the data was built yesterday
        var stale = await SummaryAsync(ok, DirectAll());
        Assert.Equal(CollectionsOutcome.InvalidRequest, stale.Outcome); Assert.Contains("built for", stale.Detail);
    }

    [Fact]
    public async Task DirectSend_ShowsTheExactReason_AndTheReviewWorkflow_ForHeldBackCustomers()
    {
        using var h = await SeededAsync(null, true, ReviewHarness.Row(1, mobile: "971500000001"), ReviewHarness.Row(2, mobile: "971500000001"), ReviewHarness.Row(3));
        var page = await ListAsync(h, DirectFilter());
        Assert.Equal(1, page.Counts.Ready); Assert.Equal(2, page.Counts.Excluded);
        var shared = page.Items.Where(i => i.ValidationStatus == "Excluded").ToList();
        Assert.All(shared, i => { var r = Assert.Single(i.Reasons); Assert.Equal(ReviewReasons.SharedPhoneMultipleUnits, r.Code); Assert.Equal(ReviewReasons.Explain(r.Code), r.Explanation); });
        Assert.Equal(1, (await SummaryAsync(h, DirectAll())).Value!.Count);                       // held back customers are never in the list that can be confirmed
    }

    [Fact]
    public async Task DirectSend_ScheduledDayCustomers_AreHeldBack_WithTheirReason()
    {
        using var h = await SeededAsync(null, advanceDay: false);                                  // 14 Oct: Current Month is scheduled for both units
        var page = await ListAsync(h, DirectFilter());
        Assert.Equal(0, page.Counts.Ready);
        Assert.All(page.Items, i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.DirectSendScheduledToday));
        Assert.Equal(0, (await SummaryAsync(h, DirectAll())).Value!.Count);
    }

    // ------------------------------------------------------------ confirmation, destination list, audit, idempotency, duplicates

    [Fact]
    public async Task Confirm_NeedsADedicatedDirectSendList_AndNothingIsApprovedWithout()
    {
        using var h = await SeededAsync(x => x.Genesys.ContactListIds.Remove("DirectSend"));
        var (summary, confirm) = await h.PrepareAsync(DirectAll());
        using var scope = h.NewScope();
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("Direct Send", refused.Detail);
        Assert.Empty(h.Scheduler.Dispatches);

        // Sharing a campaign stage's list would let that campaign dial Direct Send customers: refused.
        h.Genesys.ContactListIds["DirectSend"] = h.Genesys.ContactListIdFor(CampaignReminderType.Overdue);
        var shared = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, shared.Outcome); Assert.Contains("own", shared.Detail);
        Assert.Empty(h.Scheduler.Dispatches); Assert.Equal(2, summary.Count);
    }

    [Fact]
    public async Task Readiness_ReportsTheDirectSendListApart_SoManualAndAutoAreNeverBlockedByIt()
    {
        using var without = await SeededAsync(x => x.Genesys.ContactListIds.Remove("DirectSend"));
        using (var scope = without.NewScope())
        {
            var readiness = (await scope.Dispatch.ReadinessAsync(without.Manager, CancellationToken.None)).Value!;
            Assert.DoesNotContain(readiness.Blockers, b => b.Contains("DirectSend")); Assert.Single(readiness.DirectSendBlockers!);
            Assert.Contains("Direct Send", readiness.DirectSendBlockers![0]);
        }
        using var with = await SeededAsync();
        using (var scope = with.NewScope())
            Assert.Empty((await scope.Dispatch.ReadinessAsync(with.Manager, CancellationToken.None)).Value!.DirectSendBlockers!);
    }

    [Fact]
    public async Task Confirm_CreatesAnAuditableDispatch_ToTheDirectSendList_AndUploadsOnlyAfterTheJobRuns()
    {
        using var h = await SeededAsync();
        var (summary, confirm) = await h.PrepareAsync(DirectAll(), "direct-1");
        using var scope = h.NewScope();
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        Assert.Equal(summary.Count, accepted.Value!.ApprovedCount); Assert.Equal(h.Manager.EmployeeId, accepted.Value.InitiatedByEmployeeId);
        Assert.Empty(h.Client.Calls);                                                           // approved, not yet uploaded

        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var done = (await scope.Dispatch.GetAsync(h.Manager, accepted.Value.DispatchId, CancellationToken.None)).Value!;
        Assert.Equal("Completed", done.Status); Assert.Equal(2, done.UploadedCount);
        var batch = Assert.Single(done.Batches);
        Assert.Equal("DirectSend", batch.ReminderType); Assert.Equal(DirectList, batch.ContactListId); Assert.Equal("Direct send - manual contact list", batch.StageName);
        var call = Assert.Single(h.Client.Calls);
        Assert.Equal(DirectList, call.ContactListId); Assert.All(call.Contacts, c => { Assert.Equal("Direct Send", c.ReminderType); Assert.True(c.Callable); });
    }

    [Fact]
    public async Task Confirm_IsIdempotent_AndAnAlreadyDispatchedCustomerCannotBeSentAgain()
    {
        using var h = await SeededAsync();
        var (_, first) = await h.PrepareAsync(DirectAll(), "same-key");
        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, first, CancellationToken.None)).Outcome);
            Assert.Equal(CollectionsOutcome.Replayed, (await scope.Dispatch.ConfirmAsync(h.Manager, first, CancellationToken.None)).Outcome);   // double click / retry
        }
        Assert.Single(h.Scheduler.Dispatches);
        // Another key for the same customers: they are already in a live dispatch, so nothing is left to approve.
        Assert.Equal(0, (await SummaryAsync(h, DirectAll())).Value!.Count);
        using (var scope = h.NewScope()) Assert.False((await scope.Dispatch.ConfirmAsync(h.Manager, first with { IdempotencyKey = "other-key" }, CancellationToken.None)).IsSuccess);
        Assert.Single(h.Scheduler.Dispatches);
        var after = await ListAsync(h, DirectFilter());
        Assert.All(after.Items, i => Assert.Equal("AlreadySent", i.ValidationStatus));
    }

    [Fact]
    public async Task ContactFrequency_OneContactPerCustomerPerDay_AcrossManualAndDirectSend()
    {
        using var h = await SeededAsync();
        var manual = new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", SendDate: Today), ReviewQueryService.ModeAllMatching);
        var (_, confirm) = await h.PrepareAsync(manual, "manual-1");
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        // The same customers were just added to a Genesys list: Direct Send holds them back for the rest of the day, and says why.
        Assert.Equal(0, (await SummaryAsync(h, DirectAll())).Value!.Count);
        var page = await ListAsync(h, DirectFilter());
        Assert.All(page.Items, i => { Assert.Equal("AlreadySent", i.ValidationStatus); Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.ContactedToday); });
        // Tomorrow the limit no longer applies (the review data must be refreshed first, and the released limit is a new day).
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(2);
        await h.RefreshAsync();
        Assert.Equal(2, (await ListAsync(h, DirectFilter())).Counts.Ready);
    }

    [Fact]
    public async Task Execution_StillRevalidatesCurrentBalances_AndDropsCustomersWhoPaidAfterApproval()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(DirectAll(), "reval");
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        h.Source.Items.RemoveAll(r => r.TenantId == "T0002");                                  // customer 2 pays between approval and upload
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var call = Assert.Single(h.Client.Calls);
        Assert.Single(call.Contacts); Assert.Equal("Customer 1", call.Contacts[0].CustomerName);
    }
}
