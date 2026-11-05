using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Manual sending mode through the stored review data and the approval: it waives ONLY the schedule exclusion. Reconciliation, contact validation, permissions, repeated-call rules,
/// duplicate-send protection and the send date itself stay in force. Today is 15 Oct 2026, a day on which Current Month (14th) and Follow Up (28th) are both outside their schedule.
/// </summary>
public sealed class ManualSendingReviewTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1));                       // 15 Oct 08:00 UTC
        h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : [ReviewHarness.Row(1), ReviewHarness.Row(2)]);
        await h.RefreshAsync();
        return h;
    }

    private static SelectionRequest Selection(string mode, DateOnly? sendDate = null, string type = "CurrentMonth") =>
        new(new ReviewFilter(ReminderType: type, SendingMode: mode, SendDate: sendDate ?? Today), ReviewQueryService.ModeAllMatching);

    private static async Task<CollectionsResult<SelectionSummaryDto>> SummaryAsync(ReviewHarness h, SelectionRequest request, CollectionsCaller? caller = null)
    {
        using var scope = h.NewScope();
        return await scope.Query.SummarizeAsync(caller ?? h.Manager, request, CancellationToken.None);
    }

    // ------------------------------------------------------------ builder: the schedule exclusion and the manual markers

    private static ReviewBuildContext Context(DateOnly asOf) => new(asOf, ReviewHarness.Now, "PACT test", "AED", true, true, false,
        MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>());

    [Fact]
    public void AnOutsideScheduleRecord_StaysExcludedForOneReasonOnly_WhenNothingRepeatsItsCall()
    {
        var records = ReviewRecordBuilder.Build([ReviewHarness.Row(1)], Context(Today)).Records;
        var current = Assert.Single(records, r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal(ReviewValidationStatus.Excluded, current.ValidationStatus);
        Assert.Equal([ReviewReasons.OutsideSchedule], ReviewRecordBuilder.Parse(current.Reasons));
    }

    [Fact]
    public void ManualMarkers_RecordWhatTheRepeatedCallRulesWouldSay_WithoutChangingTheAutoStatus()
    {
        // 28 Oct: Follow Up is scheduled, Current Month is not. The same unit would be called twice if Current Month were sent manually today.
        var overlap = ReviewRecordBuilder.Build([ReviewHarness.Row(1)], Context(new DateOnly(2026, 10, 28))).Records;
        var current = Assert.Single(overlap, r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal(ReviewValidationStatus.Excluded, current.ValidationStatus);
        Assert.Contains(ReviewReasons.ManualReminderTypeOverlap, ReviewRecordBuilder.Parse(current.Reasons));
        Assert.Equal(ReviewValidationStatus.Ready, Assert.Single(overlap, r => r.ReminderType == CampaignReminderType.FollowUp).ValidationStatus);

        // Two units sharing one number: neither may be sent manually.
        var shared = ReviewRecordBuilder.Build([ReviewHarness.Row(3, mobile: "971500000001"), ReviewHarness.Row(4, mobile: "971500000001")], Context(Today)).Records
            .Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(2, shared.Count);
        Assert.All(shared, r => { Assert.Equal(ReviewValidationStatus.Excluded, r.ValidationStatus); Assert.Contains(ReviewReasons.ManualSharedPhone, ReviewRecordBuilder.Parse(r.Reasons)); });
    }

    // ------------------------------------------------------------ approval

    [Fact]
    public async Task Auto_OutsideTheSchedule_CannotBeApproved_ButManualOnTheSendDateCan()
    {
        using var h = await SeededAsync();
        Assert.Equal(0, (await SummaryAsync(h, Selection("Auto"))).Value!.Count);
        Assert.Equal(0, (await SummaryAsync(h, Selection(""))).Value!.Count);                 // no mode = Auto
        var manual = (await SummaryAsync(h, Selection("Manual"))).Value!;
        Assert.Equal(2, manual.Count);
        Assert.Equal(1000m, manual.TotalsByCurrency["AED"]);
    }

    [Fact]
    public async Task Manual_ConfirmQueuesOnlyOnTheSendDate_AndNeverDispatchesBeforeAnAuthorizedConfirm()
    {
        using var h = await SeededAsync();
        Assert.Empty(h.Scheduler.Dispatches);                                                  // preview / summary / refresh queued nothing
        var future = await SummaryAsync(h, Selection("Manual", Today.AddDays(5)));
        Assert.Equal(CollectionsOutcome.InvalidRequest, future.Outcome); Assert.Contains("future", future.Detail);
        var past = await SummaryAsync(h, Selection("Manual", Today.AddDays(-1)));
        Assert.Equal(CollectionsOutcome.InvalidRequest, past.Outcome); Assert.Contains("passed", past.Detail);
        var noDate = await SummaryAsync(h, new(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual"), ReviewQueryService.ModeAllMatching));
        Assert.Equal(CollectionsOutcome.InvalidRequest, noDate.Outcome);
        Assert.Empty(h.Scheduler.Dispatches);                                                  // a pending campaign is just not approvable: nothing was queued

        var (summary, confirm) = await h.PrepareAsync(Selection("Manual"));
        using var scope = h.NewScope();
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome); Assert.Equal(summary.Count, accepted.Value!.ApprovedCount);
        Assert.Single(h.Scheduler.Dispatches);
    }

    [Fact]
    public async Task Manual_KeepsDuplicateSendProtection_AndTheOncePerDayRule()
    {
        using var h = await SeededAsync();
        var (_, first) = await h.PrepareAsync(Selection("Manual"), "k1");
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, first, CancellationToken.None)).Outcome);
        // The same customers again (another key) are already in a live dispatch: nothing is left to approve.
        Assert.Equal(0, (await SummaryAsync(h, Selection("Manual"))).Value!.Count);
        // Another reminder type for the same units on the same day is held back as well (never two calls in one day).
        Assert.Equal(0, (await SummaryAsync(h, Selection("Manual", type: "FollowUp"))).Value!.Count);
        using (var scope = h.NewScope())
        {
            var replay = await scope.Dispatch.ConfirmAsync(h.Manager, first with { IdempotencyKey = "k2" }, CancellationToken.None);
            Assert.False(replay.IsSuccess);                                                    // the list changed (nothing Ready) or the records are already dispatched
        }
        Assert.Single(h.Scheduler.Dispatches);
    }

    [Fact]
    public async Task Manual_DoesNotBypassReconciliation_Contacts_SharedPhones_OrPermissions()
    {
        using var unreconciled = await SeededAsync(h => h.Campaign.FinancialSourceValidated = false);
        Assert.Equal(0, (await SummaryAsync(unreconciled, Selection("Manual"))).Value!.Count);

        using var noPhone = await SeededAsync(null, ReviewHarness.Row(1, mobile: "123"), ReviewHarness.Row(2, mobile: "123"));
        Assert.Equal(0, (await SummaryAsync(noPhone, Selection("Manual"))).Value!.Count);

        using var shared = await SeededAsync(null, ReviewHarness.Row(3, mobile: "971500000001"), ReviewHarness.Row(4, mobile: "971500000001"), ReviewHarness.Row(5));
        var summary = (await SummaryAsync(shared, Selection("Manual"))).Value!;
        Assert.Equal(1, summary.Count);                                                        // only the unit with its own number
        Assert.Equal(0, summary.SharedPhoneRecordCount);

        using var ok = await SeededAsync();
        Assert.Equal(CollectionsOutcome.Forbidden, (await SummaryAsync(ok, Selection("Manual"), ok.Agent)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await SummaryAsync(ok, Selection("Manual"), ok.Reporting)).Outcome);
    }

    [Fact]
    public async Task Manual_NeedsReviewDataBuiltToday()
    {
        using var h = await SeededAsync();
        h.Time.Advance(TimeSpan.FromDays(1));                                                  // 16 Oct: the data was built yesterday and is stale
        var tomorrow = await SummaryAsync(h, Selection("Manual", Today.AddDays(1)));
        Assert.Equal(CollectionsOutcome.InvalidRequest, tomorrow.Outcome);
        Assert.Contains("built for", tomorrow.Detail);
    }
}
