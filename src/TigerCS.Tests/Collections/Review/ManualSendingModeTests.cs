using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Manual sending mode (one of exactly two): an authorized person sends ANY existing campaign type at ANY time, with no send date and no stage schedule, to an existing Genesys contact list that suits it.
/// Only the schedule is waived. Reconciliation, permissions, contact and identity validation, shared phones, the legal restrictions, duplicate-send and idempotency protection and the contact-frequency
/// limit stay in force. Auto keeps the approved stage schedule and the stage amounts. The harness clock starts on 14 Oct 2026 (Current Month is scheduled); most tests advance to 15 Oct, on which no
/// stage is scheduled.
/// </summary>
public sealed class ManualSendingModeTests
{
    private const string CurrentMonthList = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1", FollowUpList = "3a91c06e-47ab-4a5e-a720-004bd5cf5bba", OverdueList = "d5f4d808-2e12-410e-8fe6-b810cca1ef4c";
    private const string LegalNoticeList = "372d81d7-6d2d-4b9e-8f09-cf2262aecfdf";
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null, bool advanceDay = true, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        if (advanceDay) { h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1); }
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : [ReviewHarness.Row(1), ReviewHarness.Row(2)]);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual(string type = "CurrentMonth", string? list = null, Func<ReviewFilter, ReviewFilter>? with = null)
    {
        var filter = new ReviewFilter(ReminderType: type, SendingMode: "Manual", ContactList: list, MinRemaining: 0m);
        return with is null ? filter : with(filter);
    }

    private static SelectionRequest All(ReviewFilter filter) => new(filter, ReviewQueryService.ModeAllMatching);

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

    // ------------------------------------------------------------ exactly two modes

    [Fact]
    public void ThereAreExactlyTwoSendingModes_AndAnythingElseIsRefused()
    {
        foreach (var (text, expected) in new[] { ("", "Auto"), ("auto", "Auto"), ("Auto", "Auto"), ("Manual", "Manual"), ("manual", "Manual") })
        {
            Assert.True(CollectionsSendingModes.TryParse(text, out var mode)); Assert.Equal(expected, mode);
        }
        foreach (var other in new[] { "DirectSend", "Direct", "Instant", "Scheduled" }) Assert.False(CollectionsSendingModes.TryParse(other, out _));
        Assert.True(CollectionsSendingModes.IsManual("Manual")); Assert.False(CollectionsSendingModes.IsManual("Auto")); Assert.False(CollectionsSendingModes.IsManual(null));
        Assert.Equal(5, Enum.GetValues<CampaignReminderType>().Length);                                    // no Direct Send type exists any more
    }

    [Fact]
    public async Task AnUnknownSendingMode_IsRefusedByTheApproval()
    {
        using var h = await SeededAsync();
        var refused = await SummaryAsync(h, All(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "DirectSend")));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("Manual or Auto", refused.Detail);
    }

    // ------------------------------------------------------------ builder: the schedule is the only rule waived

    [Fact]
    public void EveryStageRecord_CarriesTheCustomersInstalments_AndNoExtraRecordExists()
    {
        var rows = new[] { ReviewHarness.Row(1, 500m, day: 20), ReviewHarness.Row(1, 300m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1B" } };
        var records = ReviewRecordBuilder.Build(rows, Context(Today)).Records;
        Assert.Equal([CampaignReminderType.CurrentMonth, CampaignReminderType.FollowUp, CampaignReminderType.Overdue], records.Select(r => r.ReminderType).Order());   // one per stage that qualifies
        Assert.All(records, r => Assert.Equal("20260810=300;20261020=500", r.InstalmentsJson));
        // Auto still sees the stage amounts: Current Month = this month's instalment only, Overdue = the August one.
        Assert.Equal(500m, records.Single(r => r.ReminderType == CampaignReminderType.CurrentMonth).RemainingAmount);
        Assert.Equal(300m, records.Single(r => r.ReminderType == CampaignReminderType.Overdue).RemainingAmount);
    }

    [Fact]
    public void AnOutsideScheduleRecord_IsExcludedForOneReasonOnly_ButManuallySendable()
    {
        var records = ReviewRecordBuilder.Build([ReviewHarness.Row(1)], Context(Today)).Records;
        var current = Assert.Single(records, r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal(ReviewValidationStatus.Excluded, current.ValidationStatus);                          // Auto: not sent outside the schedule
        Assert.Equal([ReviewReasons.OutsideSchedule], ReviewRecordBuilder.Parse(current.Reasons));
        Assert.True(ReviewRecordBuilder.IsManuallySendable(current));                                     // Manual: the schedule is waived
        var scheduled = ReviewRecordBuilder.Build([ReviewHarness.Row(1)], Context(new DateOnly(2026, 10, 14))).Records.Single(r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal(ReviewValidationStatus.Ready, scheduled.ValidationStatus); Assert.True(ReviewRecordBuilder.IsManuallySendable(scheduled));
    }

    [Fact]
    public void ManualWaivesOnlyTheSchedule_EveryOtherReasonKeepsTheRecordOut()
    {
        // Two customers on one number, a bad contact, an unreconciled source, an internal legal referral: none of them is manually sendable.
        var shared = ReviewRecordBuilder.Build([ReviewHarness.Row(3, mobile: "971500000001"), ReviewHarness.Row(4, mobile: "971500000001")], Context(Today)).Records
            .Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(2, shared.Count);
        Assert.All(shared, r => { Assert.Contains(ReviewReasons.ManualSharedPhone, ReviewRecordBuilder.Parse(r.Reasons)); Assert.False(ReviewRecordBuilder.IsManuallySendable(r)); });
        var noPhone = ReviewRecordBuilder.Build([ReviewHarness.Row(5, mobile: "123")], Context(Today)).Records.First(r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.False(ReviewRecordBuilder.IsManuallySendable(noPhone)); Assert.Contains(ReviewReasons.NoValidContact, ReviewRecordBuilder.Parse(noPhone.Reasons));
        var unreconciled = ReviewRecordBuilder.Build([ReviewHarness.Row(6)], Context(Today) with { SourceReconciled = false }).Records.First();
        Assert.True(ReviewRecordBuilder.IsManuallySendable(unreconciled));                                 // the source gate is the send's, not the record's (ReconciliationGateTests)
        // 28 Oct: Follow Up is scheduled, so a manual Current Month for the same customer would call twice.
        var overlap = ReviewRecordBuilder.Build([ReviewHarness.Row(7)], Context(new DateOnly(2026, 10, 28))).Records.Single(r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Contains(ReviewReasons.ManualReminderTypeOverlap, ReviewRecordBuilder.Parse(overlap.Reasons)); Assert.False(ReviewRecordBuilder.IsManuallySendable(overlap));
    }

    // ------------------------------------------------------------ any campaign type, any time, no send date

    [Fact]
    public async Task Manual_SendsAnyCampaignTypeAtAnyTime_WithoutASendDate_WhileAutoWaitsForItsSchedule()
    {
        using var h = await SeededAsync();                                                              // 15 Oct: no stage is scheduled today
        Assert.Equal(0, (await SummaryAsync(h, new(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Auto"), ReviewQueryService.ModeAllMatching))).Value!.Count);
        Assert.Equal(0, (await SummaryAsync(h, h.All(new ReviewFilter(ReminderType: "CurrentMonth")))).Value!.Count);                 // no mode = Auto
        foreach (var type in new[] { "CurrentMonth", "FollowUp" })
        {
            var summary = (await SummaryAsync(h, All(Manual(type)))).Value!;
            Assert.Equal(2, summary.Count); Assert.Equal([type], summary.CountByReminderType.Keys);
            Assert.Equal(1000m, summary.TotalsByCurrency["AED"]);
        }
        Assert.Equal(0, (await SummaryAsync(h, All(Manual("Overdue")))).Value!.Count);                 // no customer belongs to the Overdue campaign: nothing is invented
        Assert.Empty(h.Scheduler.Dispatches); Assert.Empty(h.Client.Calls);                             // listing and summarising never send anything
    }

    [Fact]
    public async Task Auto_KeepsTheApprovedScheduleAndTheStageAmounts()
    {
        var rows = new[] { ReviewHarness.Row(1, 500m, day: 20), ReviewHarness.Row(1, 300m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1B" } };
        using var scheduled = await SeededAsync(null, false, rows);                                     // 14 Oct: Current Month is scheduled
        var auto = (await SummaryAsync(scheduled, new(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Auto"), ReviewQueryService.ModeAllMatching))).Value!;
        Assert.Equal(1, auto.Count); Assert.Equal(500m, auto.TotalsByCurrency["AED"]);                  // the stage amount: this month's instalment only
        var manual = (await SummaryAsync(scheduled, All(Manual("CurrentMonth")))).Value!;
        Assert.Equal(800m, manual.TotalsByCurrency["AED"]);                                             // Manual: what the filters select (Both, no dates) is the whole unpaid balance
        Assert.Equal("Current month reminder - day 14", CampaignReminderTypes.StageName(CampaignReminderType.CurrentMonth));
        // The list of a Manual request shows the schedule-held record as Ready; the unfiltered stage list still shows it as Excluded (outside schedule).
        using var offDay = await SeededAsync();
        var manualList = await ListAsync(offDay, Manual());
        Assert.Equal(2, manualList.Counts.Ready); Assert.All(manualList.Items, i => { Assert.Equal("Ready", i.ValidationStatus); Assert.DoesNotContain(i.Reasons, r => r.Code == ReviewReasons.OutsideSchedule); });
        var stageList = await ListAsync(offDay, new ReviewFilter(ReminderType: "CurrentMonth"));
        Assert.Equal(0, stageList.Counts.Ready); Assert.All(stageList.Items, i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.OutsideSchedule));
    }

    [Fact]
    public async Task Manual_NeedsTheCampaignType_AndTheReviewDataBuiltToday()
    {
        using var h = await SeededAsync();
        var none = await SummaryAsync(h, All(new ReviewFilter(SendingMode: "Manual")));
        Assert.Equal(CollectionsOutcome.InvalidRequest, none.Outcome); Assert.Contains("campaign type", none.Detail);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await ListAsyncResult(h, new ReviewFilter(SendingMode: "Manual"))).Outcome);
        h.Time.Advance(TimeSpan.FromDays(1));                                                           // 16 Oct: the data was built yesterday
        var stale = await SummaryAsync(h, All(Manual()));
        Assert.Equal(CollectionsOutcome.InvalidRequest, stale.Outcome); Assert.Contains("built for", stale.Detail);
    }

    private static async Task<CollectionsResult<ReviewPageDto>> ListAsyncResult(ReviewHarness h, ReviewFilter filter)
    {
        using var scope = h.NewScope();
        return await scope.Query.QueryAsync(h.Manager, filter, 1, 100, CancellationToken.None);
    }

    // ------------------------------------------------------------ filters, selection

    [Fact]
    public async Task Manual_FiltersByTower_PaymentStatus_DateRange_AndMinimumTotal()
    {
        var rows = new[]
        {
            ReviewHarness.Row(1, 900m, day: 20), ReviewHarness.Row(2, 150m, day: 20),
            ReviewHarness.Row(3, 100m, day: 20), ReviewHarness.Row(3, 500m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-3B" },
            ReviewHarness.Row(4, 700m, day: 20) with { TowerId = 7, TowerNumber = "124", TowerName = "Tower 124" }
        };
        using var h = await SeededAsync(null, true, rows);
        var all = await ListAsync(h, Manual());
        Assert.Equal(4, all.Counts.Total); Assert.Equal(600m, all.Items.Single(i => i.UnitCode == "TP140-3").RemainingAmount);              // the whole unpaid balance of unit 3
        Assert.Equal(3, (await ListAsync(h, Manual(with: f => f with { MinRemaining = 200m }))).Counts.Total);                                // Minimum Total (strictly greater), on the filtered amount
        var due = await ListAsync(h, Manual(with: f => f with { DueStatus = "Due" }));                                                         // Payment status Due: only the instalments due this month
        Assert.Equal(4, due.Counts.Total); Assert.Equal(100m, due.Items.Single(i => i.UnitCode == "TP140-3").RemainingAmount);
        var overdue = await ListAsync(h, Manual(with: f => f with { DueStatus = "Overdue" }));                                                 // Overdue: only unit 3 has any
        Assert.Equal(["TP140-3"], overdue.Items.Select(i => i.UnitCode)); Assert.Equal(500m, overdue.Items[0].RemainingAmount);
        var range = await ListAsync(h, Manual(with: f => f with { DueFrom = new DateOnly(2026, 8, 1), DueTo = new DateOnly(2026, 8, 31) }));   // Date range
        Assert.Equal(["TP140-3"], range.Items.Select(i => i.UnitCode)); Assert.Equal(500m, range.Items[0].RemainingAmount);
        var tower = await ListAsync(h, Manual(with: f => f with { TowerId = 7 }));                                                             // Tower
        Assert.Equal(["TP140-4"], tower.Items.Select(i => i.UnitCode));
    }

    [Fact]
    public async Task SelectedCustomers_AreExactlyWhatIsResolved_AndSelectAllCoversEveryEligibleCustomer()
    {
        using var h = await SeededAsync(null, true, ReviewHarness.Row(1), ReviewHarness.Row(2), ReviewHarness.Row(3));
        var first = (await ListAsync(h, Manual())).Items[0];
        var picked = new SelectionRequest(Manual(), ReviewQueryService.ModeSelectedUnits, null, null, 1, 50, [ReviewQueryService.UnitKey(first.CompanyId, first.TenantId!, first.UnitCode)]);
        var one = (await SummaryAsync(h, picked)).Value!;
        Assert.Equal(1, one.Count); Assert.Equal(first.UnitCode, one.Contacts.Single().UnitCode);
        Assert.Equal(2, (await SummaryAsync(h, All(Manual()) with { ExcludedKeys = [first.RecordKey] })).Value!.Count);
        Assert.Equal(3, (await SummaryAsync(h, All(Manual()))).Value!.Count);                          // Select all eligible customers
        // Explicit record keys never reach a Manual list through the unfiltered path either.
        var keys = (await ListAsync(h, Manual())).Items.Select(i => i.RecordKey).ToList();
        Assert.Equal(0, (await SummaryAsync(h, new SelectionRequest(null, ReviewQueryService.ModeSelectedKeys, keys))).Value!.Count);        // Excluded for the schedule outside Manual
    }

    // ------------------------------------------------------------ safety rules are not bypassed

    [Fact]
    public async Task Manual_DoesNotBypassReconciliation_Permissions_Contacts_OrSharedPhones()
    {
        using var unreconciled = await SeededAsync(h => h.Campaign.FinancialSourceValidated = false);
        var summary = (await SummaryAsync(unreconciled, All(Manual()))).Value!;
        Assert.False(summary.SourceReconciled);                                                            // the gate is the source's, said once
        var listed = await ListAsync(unreconciled, Manual());
        Assert.All(listed.Items, i => Assert.DoesNotContain(i.Reasons, r => r.Code == ReviewReasons.SourceReconciliationRequired));   // customers keep their own status
        using (var scope = unreconciled.NewScope())
        {
            var refused = await scope.Dispatch.ConfirmAsync(unreconciled.Manager, new ConfirmDispatchRequest(All(Manual()), summary.Count, summary.Fingerprint, "unreconciled", true), CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("not reconciled", refused.Detail); Assert.Empty(unreconciled.Scheduler.Dispatches);
        }

        using var noPhone = await SeededAsync(null, true, ReviewHarness.Row(1, mobile: "123"), ReviewHarness.Row(2, mobile: "123"));
        Assert.Equal(0, (await SummaryAsync(noPhone, All(Manual()))).Value!.Count);

        using var shared = await SeededAsync(null, true, ReviewHarness.Row(3, mobile: "971500000001"), ReviewHarness.Row(4, mobile: "971500000001"), ReviewHarness.Row(5));
        var page = await ListAsync(shared, Manual());
        Assert.Equal(1, page.Counts.Ready);
        Assert.All(page.Items.Where(i => i.ValidationStatus != "Ready"), i => { Assert.Equal("Excluded", i.ValidationStatus); Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.ManualSharedPhone); Assert.DoesNotContain(i.Reasons, r => r.Code == ReviewReasons.OutsideSchedule); });
        Assert.Equal(1, (await SummaryAsync(shared, All(Manual()))).Value!.Count);

        using var ok = await SeededAsync();
        Assert.Equal(CollectionsOutcome.Forbidden, (await SummaryAsync(ok, All(Manual()), ok.Agent)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await SummaryAsync(ok, All(Manual()), ok.Reporting)).Outcome);
    }

    [Fact]
    public async Task LegalRestrictions_StayInForce_ForEveryCampaignType()
    {
        // A legal notice (last month, above the threshold) can be sent manually at any time, but only once legal notices are released; an internal legal referral never goes to a customer.
        var legal = ReviewHarness.Row(1, 25000m) with { DueDate = new DateTime(2026, 6, 10) };                         // qualifies for Overdue, Legal Notice? (not last month) and Legal referral (3+ months, > 20,000)
        var notice = ReviewHarness.Row(2, 2000m) with { DueDate = new DateTime(2026, 9, 10) };
        using var released = await SeededAsync(null, true, legal, notice);
        Assert.Equal(["TP140-2"], (await ListAsync(released, Manual("LegalNotice"))).Items.Select(i => i.UnitCode));
        Assert.Equal(1, (await SummaryAsync(released, All(Manual("LegalNotice", list: "LegalNotice")))).Value!.Count);
        var referral = await ListAsync(released, Manual("LegalCase"));
        Assert.Equal(["TP140-1"], referral.Items.Select(i => i.UnitCode)); Assert.Equal(0, referral.Counts.Ready);
        Assert.Contains(referral.Items[0].Reasons, r => r.Code == ReviewReasons.LegalCaseNotApproved);
        Assert.Equal(0, (await SummaryAsync(released, All(Manual("LegalCase")))).Value!.Count);                       // never sendable to a customer

        using var notReleased = await SeededAsync(h => h.Campaign.LegalNoticeExportEnabled = false, true, notice);
        var held = await ListAsync(notReleased, Manual("LegalNotice"));
        Assert.Equal(0, held.Counts.Ready); Assert.Contains(held.Items.Single().Reasons, r => r.Code == ReviewReasons.LegalNoticeReleaseRequired);
        Assert.Equal(0, (await SummaryAsync(notReleased, All(Manual("LegalNotice")))).Value!.Count);
    }

    // ------------------------------------------------------------ contact lists

    [Theory]
    [InlineData("CurrentMonth", null, CurrentMonthList, "CurrentMonth")]            // a campaign's own list is the default
    [InlineData("CurrentMonth", "FollowUp", FollowUpList, "FollowUp")]              // a Current Month reminder may go to the Follow Up list ...
    [InlineData("FollowUp", "CurrentMonth", CurrentMonthList, "CurrentMonth")]      // ... and a Follow Up reminder to the Current Month list
    [InlineData("FollowUp", null, FollowUpList, "FollowUp")]
    public async Task TheContactList_IsTheCampaignsOwn_OrAnotherExistingListThatSuitsIt(string type, string? chosen, string listId, string expectedName)
    {
        using var h = await SeededAsync();
        var (summary, confirm) = await h.PrepareAsync(All(Manual(type, chosen)), $"list-{type}-{chosen}");
        Assert.Equal(listId, summary.ContactListByReminderType![type]); Assert.Equal(expectedName, summary.ContactListName);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var call = Assert.Single(h.Client.Calls);
        Assert.Equal(listId, call.ContactListId);                                                      // uploaded to exactly the chosen existing list
        Assert.All(call.Contacts, c => Assert.Equal(CampaignReminderTypes.DefaultLabel(Enum.Parse<CampaignReminderType>(type)), c.ReminderType));
    }

    [Theory]
    [InlineData("CurrentMonth", "LegalNotice")]
    [InlineData("CurrentMonth", "Nonsense")]
    [InlineData("FollowUp", "LegalCase")]
    [InlineData("LegalNotice", "CurrentMonth")]                                     // legal wording never lands in a reminder list
    [InlineData("LegalNotice", "FollowUp")]
    public async Task AListThatDoesNotSuitTheCampaign_IsRefused(string type, string list)
    {
        using var h = await SeededAsync(null, true, ReviewHarness.Row(1), ReviewHarness.Row(2, 2000m) with { DueDate = new DateTime(2026, 9, 10) });
        var refused = await SummaryAsync(h, All(Manual(type, list)));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("cannot be sent to that contact list", refused.Detail);
        Assert.Empty(h.Scheduler.Dispatches);
    }

    [Fact]
    public async Task Confirm_AuditsTheDispatch_WithTheCampaign_TheChosenList_AndWhoApproved()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(All(Manual("CurrentMonth", "FollowUp")), "audit");
        using var scope = h.NewScope();
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome); Assert.Equal(h.Manager.EmployeeId, accepted.Value!.InitiatedByEmployeeId);
        Assert.Empty(h.Client.Calls);                                                                    // approved, never automatic
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var done = (await scope.Dispatch.GetAsync(h.Manager, accepted.Value.DispatchId, CancellationToken.None)).Value!;
        Assert.Equal("Completed", done.Status); Assert.Equal(2, done.UploadedCount);
        var batch = Assert.Single(done.Batches);
        Assert.Equal("CurrentMonth", batch.ReminderType); Assert.Equal(FollowUpList, batch.ContactListId); Assert.Equal("Current month reminder - day 14", batch.StageName);   // purpose + list id
        using var context = h.CreateContext();
        var stored = context.CollectionsDispatches.Single();
        Assert.Contains("\"ContactList\":\"FollowUp\"", stored.FilterJson); Assert.Contains("\"SendingMode\":\"Manual\"", stored.FilterJson); Assert.Contains("\"ReminderType\":\"CurrentMonth\"", stored.FilterJson);
        Assert.True(h.Client.ListChecks.Contains(FollowUpList) && h.Client.CampaignChecks.Contains(FollowUpList));    // the list was validated in Genesys
    }

    [Fact]
    public async Task TheDestination_MustHaveTheRequiredColumns_AndNoOtherActiveCampaign()
    {
        using var columns = await SeededAsync();
        columns.Client.ListColumns[CurrentMonthList] = ["Phone", "CustomerName", "Email Address", "ReminderType"];
        var (_, withColumns) = await columns.PrepareAsync(All(Manual()), "cols");
        using (var scope = columns.NewScope())
        {
            var refused = await scope.Dispatch.ConfirmAsync(columns.Manager, withColumns, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("required column", refused.Detail); Assert.Contains("AmountDue", refused.Detail);
        }
        Assert.Empty(columns.Scheduler.Dispatches);

        // The campaign this very send is meant for (its stage campaign, on its own list) is not interference ...
        using var own = await SeededAsync();
        own.Client.Campaigns.Add(new GenesysCampaignRef("c-stage", "Current month reminder - day 14", CurrentMonthList, "on"));
        var (_, ownConfirm) = await own.PrepareAsync(All(Manual()), "own");
        using (var scope = own.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(own.Manager, ownConfirm, CancellationToken.None)).Outcome);

        // ... but any other active campaign on the list is, and so is the stage campaign when the contacts go to a different list.
        using var other = await SeededAsync();
        other.Client.Campaigns.Add(new GenesysCampaignRef("c-other", "Some other campaign", CurrentMonthList, "on"));
        var (_, otherConfirm) = await other.PrepareAsync(All(Manual()), "other");
        using (var scope = other.NewScope())
        {
            var refused = await scope.Dispatch.ConfirmAsync(other.Manager, otherConfirm, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("Some other campaign", refused.Detail); Assert.Contains("active", refused.Detail);
            other.Client.Campaigns[0] = other.Client.Campaigns[0] with { Status = "off" };
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(other.Manager, otherConfirm, CancellationToken.None)).Outcome);
        }
        using var cross = await SeededAsync();
        cross.Client.Campaigns.Add(new GenesysCampaignRef("c-fu", "Follow-up reminder - day 28", FollowUpList, "on"));
        var (_, crossConfirm) = await cross.PrepareAsync(All(Manual("CurrentMonth", "FollowUp")), "cross");
        using (var scope = cross.NewScope())
        {
            var refused = await scope.Dispatch.ConfirmAsync(cross.Manager, crossConfirm, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("Follow-up reminder - day 28", refused.Detail);
        }
    }

    [Fact]
    public async Task TheDestination_IsCheckedAgainRightBeforeTheUpload_AndAnUnverifiableListIsNeverUsed()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(All(Manual()), "late");
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        h.Client.Campaigns.Add(new GenesysCampaignRef("c-late", "Switched on after approval", CurrentMonthList, "on"));
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Empty(h.Client.Calls);                                                                    // nothing reached the contact list
        var dispatch = (await scope.Dispatch.ListAsync(h.Manager, CancellationToken.None)).Value!.Single();
        Assert.Equal("CompletedWithErrors", dispatch.Status); Assert.Equal(0, dispatch.UploadedCount); Assert.Equal(2, dispatch.FailedCount);
        Assert.Contains("Switched on after approval", Assert.Single(dispatch.Batches).Error);

        using var unverifiable = await SeededAsync();
        unverifiable.Client.ListCheckScript.Enqueue(GenesysUploadOutcome.Unknown);
        var (_, second) = await unverifiable.PrepareAsync(All(Manual()), "unknown");
        using var scope2 = unverifiable.NewScope();
        var refused = await scope2.Dispatch.ConfirmAsync(unverifiable.Manager, second, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("could not be checked", refused.Detail);
        Assert.Empty(unverifiable.Scheduler.Dispatches);
    }

    // ------------------------------------------------------------ idempotency, duplicates, contact frequency

    [Fact]
    public async Task Confirm_IsIdempotent_AndADispatchedCustomerCannotBeSentAgain()
    {
        using var h = await SeededAsync();
        var (_, first) = await h.PrepareAsync(All(Manual()), "same-key");
        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, first, CancellationToken.None)).Outcome);
            Assert.Equal(CollectionsOutcome.Replayed, (await scope.Dispatch.ConfirmAsync(h.Manager, first, CancellationToken.None)).Outcome);   // double click / retry
        }
        Assert.Single(h.Scheduler.Dispatches);
        Assert.Equal(0, (await SummaryAsync(h, All(Manual()))).Value!.Count);                          // already in a live dispatch
        using (var scope = h.NewScope()) Assert.False((await scope.Dispatch.ConfirmAsync(h.Manager, first with { IdempotencyKey = "other-key" }, CancellationToken.None)).IsSuccess);
        Assert.Single(h.Scheduler.Dispatches);
        Assert.All((await ListAsync(h, Manual())).Items, i => Assert.Equal("AlreadySent", i.ValidationStatus));
    }

    [Fact]
    public async Task ContactFrequency_OneContactPerCustomerPerDay_AcrossCampaignTypes_WithItsReason()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(All(Manual("CurrentMonth")), "freq");
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        // The same customers were just added to a Genesys list: another campaign type holds them back for the rest of the day, and says why.
        Assert.Equal(0, (await SummaryAsync(h, All(Manual("FollowUp")))).Value!.Count);
        var page = await ListAsync(h, Manual("FollowUp"));
        Assert.All(page.Items, i => { Assert.Equal("AlreadySent", i.ValidationStatus); Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.ContactedToday); });
        // Tomorrow the limit no longer applies (fresh review data is needed first).
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(2);
        await h.RefreshAsync();
        Assert.Equal(2, (await ListAsync(h, Manual("FollowUp"))).Counts.Ready);
    }

    [Fact]
    public async Task Execution_StillRevalidatesCurrentBalances_AndDropsCustomersWhoPaidAfterApproval()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(All(Manual()), "reval");
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        h.Source.Items.RemoveAll(r => r.TenantId == "T0002");                                           // customer 2 pays between approval and upload
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var call = Assert.Single(h.Client.Calls);
        Assert.Single(call.Contacts); Assert.Equal("Customer 1", call.Contacts[0].CustomerName);
    }
}
