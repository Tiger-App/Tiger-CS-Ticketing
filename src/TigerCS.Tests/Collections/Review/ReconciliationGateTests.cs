using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Financial reconciliation is a property of the SOURCE, decided once (sign-off, procedure, snapshot-consistency evidence) and recorded on the run. It is a separate gate of the send:
/// refused at approval, again right before the upload, and by the Auto dispatcher. It is never stamped on a customer's record, so a customer with sound data (a known positive remainder,
/// a callable number) is Ready on the page and only the gate keeps the send closed. The harness clock is 14 Oct 2026, a scheduled day for the Current Month reminder.
/// </summary>
public sealed class ReconciliationGateTests
{
    /// <summary>The reported customer: one sales invoice (voucher SIN-6856) with two instalments on different dates, remaining amounts only (the deployed report returns no original or paid amount).</summary>
    private static PactReceivableInstalment[] Renad103() =>
    [
        ReviewHarness.Row(1, 73593m, name: "Renad Customer", project: "TP124") with { UnitId = 10103, UnitCode = "TP124-103", VoucherNumber = "SIN-6856", DueDate = new DateTime(2026, 8, 15), PlanAmount = null, TowerNumber = "124", TowerName = "Renad Tower" },
        ReviewHarness.Row(1, 441560m, name: "Renad Customer", project: "TP124") with { UnitId = 10103, UnitCode = "TP124-103", VoucherNumber = "SIN-6856", DueDate = new DateTime(2026, 9, 30), PlanAmount = null, TowerNumber = "124", TowerName = "Renad Tower" }
    ];

    /// <summary>By default the clock is moved to 15 Oct, a day on which no stage is scheduled, so the duplicate-call rules (another campaign scheduled today) do not take part in these tests.</summary>
    private static async Task<ReviewHarness> SeededAsync(bool reconciled, bool scheduledDay = false, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        if (!scheduledDay) { h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1); }
        h.Campaign.FinancialSourceValidated = reconciled;
        h.Source.Items.AddRange(rows.Length > 0 ? rows : [ReviewHarness.Row(1, 500m), ReviewHarness.Row(2, 700m)]);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual() => new(ReminderType: "Overdue", SendingMode: "Manual", MinRemaining: 0m);
    private static SelectionRequest All(ReviewFilter filter) => new(filter, ReviewQueryService.ModeAllMatching);

    // ------------------------------------------------------------ the reported customer

    [Fact]
    public async Task TwoInstalmentsOfOneInvoice_OnDifferentDates_AreOneCustomerRecord_WithTheSumAndTheEarliestDate_AndNoDuplicateFlag()
    {
        using var h = await SeededAsync(reconciled: true, rows: Renad103());
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        var unit = Assert.Single(page.Items);
        Assert.Equal(("TP124-103", "Renad Tower", "Ready", "Unpaid"), (unit.UnitCode, unit.TowerName, unit.ValidationStatus, unit.PaymentStatus));
        Assert.Equal(515153m, unit.RemainingAmount); Assert.Equal(new DateOnly(2026, 8, 15), unit.DueDate);
        Assert.Empty(unit.Reasons);                                                                          // same voucher on two dates = two instalments, not a duplicate source record
        // By contrast, the same instalment repeated on ONE date cannot be told from a duplicated allocation: the Manual calculation refuses to sum it (no amount, never a guess).
        var repeated = Renad103().Concat([Renad103()[1]]).Select(r => new FilterInstalment(DateOnly.FromDateTime(r.DueDate), r.Amount)).ToList();
        var ambiguous = FilteredAmounts.Calculate(repeated, new AmountCriteria(CampaignPaymentFilter.Both, null, null, null), new DateOnly(2026, 10, 15));
        Assert.Equal((FilteredAmount.Ambiguous, 0m), (ambiguous.Reason, ambiguous.Amount));
    }

    [Fact]
    public void OriginalAndPaidAmounts_AreNotNeeded_TheRemainderClassifiesEveryInstalment()
    {
        // The deployed PACT report (layouts 1 and 3) returns Amount and Status only: PlanAmount and AllocatedAmount are null at the source, not merely hidden by a page.
        foreach (var row in Renad103()) { Assert.Null(row.PlanAmount); Assert.Null(row.AllocatedAmount); }
        Assert.All(Renad103(), r => Assert.Equal(ReviewPaymentStatus.Unpaid, PaymentStatusRules.Derive(r.SourceStatus, r.Amount, r.PlanAmount, r.AllocatedAmount)));
        Assert.All(Renad103(), r => Assert.Equal("Unpaid", CollectionsPaymentFilters.Classify(r.Amount, null, null, r.SourceStatus, false)));
        Assert.All(Renad103(), r => Assert.Equal("Unpaid", PactReceivableCustomersAppService.PaymentStatus(r.SourceStatus, r.Amount, new Dictionary<string, string>())));
        var amounts = FilteredAmounts.Calculate(Renad103().Select(r => new FilterInstalment(DateOnly.FromDateTime(r.DueDate), r.Amount)).ToList(),
            new AmountCriteria(CampaignPaymentFilter.Both, null, null, null), new DateOnly(2026, 10, 14));
        Assert.Equal((515153m, 0m, 515153m, new DateOnly(2026, 8, 15)), (amounts.Amount, amounts.DueAmount, amounts.OverdueAmount, amounts.EarliestDue));   // both before October: Overdue
    }

    // ------------------------------------------------------------ the gate, separate from the customers

    [Fact]
    public async Task WithoutReconciliation_TheCustomerIsReadyOnThePage_TheRunSaysNotReconciled_AndTheApprovalIsRefused()
    {
        using var h = await SeededAsync(reconciled: false, rows: Renad103());
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        Assert.False(page.Run!.SourceReconciled);                                                            // the gate, once, on the run
        var unit = Assert.Single(page.Items);
        Assert.Equal("Ready", unit.ValidationStatus); Assert.Empty(unit.Reasons); Assert.Equal(1, page.Counts.Ready);   // not "Needs review" for a cause that is not the customer's
        var summary = (await scope.Query.SummarizeAsync(h.Manager, All(Manual()), CancellationToken.None)).Value!;
        Assert.Equal(1, summary.Count); Assert.False(summary.SourceReconciled);
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(All(Manual()), summary.Count, summary.Fingerprint, "gate-1", true), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("not reconciled", refused.Detail);
        Assert.Empty(h.Scheduler.Dispatches); Assert.Empty(h.Client.Calls);
        // Auto refuses for the same single reason, before it looks at any customer (on a scheduled day, with customers that would otherwise be dispatched).
        using var scheduled = await SeededAsync(reconciled: false, scheduledDay: true);
        scheduled.Campaign.AutoSendingEnabled = true;
        using var auto = scheduled.NewScope();
        Assert.Equal("NotReconciled", (await auto.Auto.RunAsync(CancellationToken.None)).Stages.Single(s => s.Stage == "CurrentMonth").Outcome);
        Assert.Empty(scheduled.Scheduler.Dispatches);
    }

    [Fact]
    public async Task ASignOffWithdrawnAfterApproval_StopsTheUploadBeforeAnythingIsSent()
    {
        using var h = await SeededAsync(reconciled: true);
        var (summary, confirm) = await h.PrepareAsync(All(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m)), "withdrawn");
        Assert.Equal(2, summary.Count);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        h.Campaign.FinancialSourceValidated = false;                                                        // the gate changes between approval and execution
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var dispatch = (await scope.Store.ListDispatchesAsync(1, CancellationToken.None)).Single();
        Assert.Equal(DispatchStatus.ReviewRequired, dispatch.Status); Assert.Contains("not reconciled", dispatch.StatusReason);
        Assert.Empty(h.Client.Calls);
    }

    [Fact]
    public async Task WithReconciliation_TheSameCustomerIsSent_NothingElseChanged()
    {
        using var h = await SeededAsync(reconciled: true, rows: Renad103());
        var (summary, confirm) = await h.PrepareAsync(All(Manual()), "renad");
        Assert.Equal(1, summary.Count); Assert.True(summary.SourceReconciled);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var contact = Assert.Single(h.Client.Calls.Single().Contacts);
        Assert.Equal(("515153.00", "2026-08-15"), (contact.AmountDue, contact.DueDate));
    }

    [Fact]
    public async Task TheOtherSafetyRules_StillHoldACustomerBack_WhenTheSourceIsReconciled()
    {
        using var h = await SeededAsync(reconciled: true, rows: [ReviewHarness.Row(1, 500m, mobile: "123"), ReviewHarness.Row(2, 400m, status: "Paid"), ReviewHarness.Row(3, 700m)]);
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(1, page.Counts.Ready);
        Assert.Contains(page.Items.Single(i => i.CustomerName == "Customer 1").Reasons, r => r.Code == ReviewReasons.NoValidContact);
        Assert.Contains(page.Items.Single(i => i.CustomerName == "Customer 2").Reasons, r => r.Code == ReviewReasons.ContradictoryPaymentStatus);
    }

    private static ReviewBuildContext Context(bool reconciled) => new(new DateOnly(2026, 10, 14), ReviewHarness.Now, "PACT test", "AED", reconciled, true, false,
        MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>());
}
