using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// PACT is the source and the snapshot its synchronized copy: a difference blocks the records or the company it concerns, never the whole Collections workflow. The gate is decided per
/// company from the automated evidence, a unit whose snapshot differs persistently from PACT is held back on its own, and a snapshot republished after the evidence was taken is not
/// sent on until it is checked again. The harness clock is 14 Oct 2026, a scheduled day for the Current Month reminder; every customer below qualifies for it.
/// </summary>
public sealed class PerCompanyGateTests
{
    private static ReviewFilter Current(int? company = null) => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m, CompanyId: company);

    /// <summary>Records one conclusive result per company on the evidence: Passed, or Failed after a successful refresh with the differing units listed (a persistent mismatch).</summary>
    private static void Evidence(ReviewHarness h, DateTime at, params (int Company, string State, (string Tenant, long UnitId, string Code)[] Held)[] companies)
    {
        h.Evidence ??= new ReconciliationHistory();
        var run = new ReconciliationRunDto(Guid.NewGuid(), ReconciliationStates.Running, at, null, new(2014, 1, 1), new(2026, 10, 31), null, 0.01m, "test", []);
        Assert.True(h.Evidence.TryBegin(run, TimeSpan.Zero, out _));
        var results = companies.Select(c =>
        {
            var diffs = c.Held.Select(u => new ReconUnitDiff(u.Tenant, u.UnitId, u.Code, 1000m, 400m, 1, 1, 0m, 0m, 1000m, 400m,
                [new ReconInstalmentDiff(ReconDifferenceKinds.AmountMismatch, "V", new(2026, 10, 20), new(2026, 10, 20), 1000m, 400m, false)], "1 AmountMismatch.")).ToList();
            var comparison = new ReconComparison(10, 10, 10, 10, 5000m, 4400m, 10 - diffs.Count, diffs.Count, 0, 0, diffs.Count > 0 ? 600m : 0m, diffs, false);
            var recovery = c.State == ReconciliationStates.Failed ? new ReconciliationRecoveryDto(ReconciliationRecoveryReasons.Differences, "Succeeded", null, true, diffs.Count, false, at.AddMinutes(-20), at.AddMinutes(-1)) : null;
            return new ReconciliationCompanyResultDto(c.Company, SourceGate.CompanyName(c.Company), c.State, c.State == ReconciliationStates.Failed ? $"{diffs.Count} unit(s) differ." : null,
                at, at.AddSeconds(5), at.AddMinutes(-1), $"dbo.p{c.Company}AccountReceivables", comparison, null, recovery);
        }).ToList();
        h.Evidence.Replace(run with { Status = results.Any(r => r.Status == ReconciliationStates.Failed) ? ReconciliationStates.Failed : ReconciliationStates.Passed, FinishedUtc = at.AddSeconds(5), Companies = results });
    }

    [Fact]
    public async Task AUnitThatDiffersPersistently_IsHeldBackAlone_EveryOtherCustomerOfBothCompaniesIsReadyAndSent()
    {
        using var h = new ReviewHarness();
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m), ReviewHarness.Row(2, 700m), ReviewHarness.Row(3, 900m, company: 32)]);
        Evidence(h, ReviewHarness.Now.AddMinutes(-5), (4, ReconciliationStates.Failed, [("T0002", 1002L, "TP140-2")]), (32, ReconciliationStates.Passed, []));
        var run = await h.RefreshAsync();
        Assert.True(run.SourceReconciled);                                                              // both companies are open: the gate is not closed by one unit
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Current(), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(3, page.Items.Count);
        var held = page.Items.Single(i => i.CustomerName == "Customer 2");
        Assert.Equal("NeedsReview", held.ValidationStatus);
        Assert.Equal(ReviewReasons.SourceMismatchUnresolved, Assert.Single(held.Reasons).Code);
        Assert.Contains("refreshing the snapshot did not resolve", ReviewReasons.Explain(ReviewReasons.SourceMismatchUnresolved));
        Assert.All(page.Items.Where(i => i.CustomerName != "Customer 2"), i => Assert.Equal("Ready", i.ValidationStatus));
        Assert.Equal(2, page.Counts.Ready);
        // The send carries the two consistent customers and nothing of the held unit.
        var (summary, confirm) = await h.PrepareAsync(h.All(Current()), "held");
        Assert.Equal(2, summary.Count); Assert.True(summary.SourceReconciled);
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var sent = h.Client.Calls.SelectMany(c => c.Contacts).Select(c => c.CustomerName).Order().ToList();
        Assert.Equal(["Customer 1", "Customer 3"], sent);
    }

    [Fact]
    public async Task ACompanyWithoutEvidence_IsClosedOnItsOwn_TheOtherCompanyIsApprovedAndSent()
    {
        using var h = new ReviewHarness();
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m), ReviewHarness.Row(3, 900m, company: 32)]);
        Evidence(h, ReviewHarness.Now.AddMinutes(-5), (4, ReconciliationStates.Passed, []));             // Sharjah: never checked
        var run = await h.RefreshAsync();
        Assert.False(run.SourceReconciled);                                                             // "both companies" is not true ...
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Current(), 1, 50, CancellationToken.None)).Value!;
        Assert.All(page.Items, i => Assert.Equal("Ready", i.ValidationStatus));                        // ... and no customer is stamped for it
        // A list that includes Sharjah is refused, naming Sharjah and saying Dubai is not held back.
        var (both, confirmBoth) = await h.PrepareAsync(h.All(Current()), "both");
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, confirmBoth, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome);
        Assert.Contains("not reconciled for Tiger Group Sharjah", refused.Detail); Assert.Contains("no snapshot-consistency check has been run", refused.Detail);
        Assert.DoesNotContain("not reconciled for Tiger Group Dubai", refused.Detail);
        Assert.Empty(h.Scheduler.Dispatches);
        // Dubai alone goes through.
        var (dubai, confirmDubai) = await h.PrepareAsync(h.All(Current(4)), "dubai");
        Assert.Equal(1, dubai.Count);
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirmDubai, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal("Customer 1", Assert.Single(h.Client.Calls.Single().Contacts).CustomerName);
    }

    [Fact]
    public async Task Auto_DispatchesTheReconciledCompany_AndReportsTheOtherAsNotReconciled()
    {
        using var h = new ReviewHarness();
        h.Campaign.AutoSendingEnabled = true;
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m), ReviewHarness.Row(3, 900m, company: 32)]);
        Evidence(h, ReviewHarness.Now.AddMinutes(-5), (4, ReconciliationStates.Passed, []));
        using var scope = h.NewScope();
        var stage = (await scope.Auto.RunAsync(CancellationToken.None)).Stages.Single(s => s.Stage == "CurrentMonth");
        Assert.Equal(("Dispatched", 1), (stage.Outcome, stage.Count));                                   // Dubai's customer; Sharjah's waits for its check
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal("Customer 1", Assert.Single(h.Client.Calls.Single().Contacts).CustomerName);
        // With nothing reconciled at all the stage says so, once, and nothing is dispatched.
        using var none = new ReviewHarness();
        none.Campaign.AutoSendingEnabled = true; none.Evidence = new ReconciliationHistory();
        none.Source.Items.AddRange([ReviewHarness.Row(1, 500m)]);
        using var noneScope = none.NewScope();
        Assert.Equal("NotReconciled", (await noneScope.Auto.RunAsync(CancellationToken.None)).Stages.Single(s => s.Stage == "CurrentMonth").Outcome);
        Assert.Empty(none.Scheduler.Dispatches);
    }

    [Fact]
    public async Task ASnapshotRepublishedBetweenApprovalAndUpload_IsNotSentOn_UntilItIsCheckedAgain()
    {
        using var h = new ReviewHarness();
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m), ReviewHarness.Row(3, 900m, company: 32)]);
        Evidence(h, ReviewHarness.Now.AddMinutes(-5), (4, ReconciliationStates.Passed, []), (32, ReconciliationStates.Passed, []));
        await h.RefreshAsync();
        var (summary, confirm) = await h.PrepareAsync(h.All(Current()), "race");
        Assert.Equal(2, summary.Count);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        // The refresh job publishes Dubai again before the upload runs (the race): the evidence of Dubai is superseded, so the dispatch stops before anything is sent.
        h.Evidence!.NoteSnapshotPublished(4, ReviewHarness.Now.AddMinutes(1));
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var dispatch = (await scope.Store.ListDispatchesAsync(1, CancellationToken.None)).Single();
        Assert.Equal(DispatchStatus.ReviewRequired, dispatch.Status);
        Assert.Contains("not reconciled for Tiger Group Dubai", dispatch.StatusReason); Assert.Contains("republished", dispatch.StatusReason);
        Assert.Empty(h.Client.Calls);
        // Checked again (the after-refresh run passes): a new approval is sent.
        Evidence(h, ReviewHarness.Now.AddMinutes(2), (4, ReconciliationStates.Passed, []));
        var (_, again) = await h.PrepareAsync(h.All(Current()), "race-2");
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, again, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
        Assert.Equal(2, h.Client.Calls.Single().Contacts.Count);
    }

    [Fact]
    public async Task AUnitHeldAfterApproval_IsAChangedRecord_AndStopsTheUpload()
    {
        using var h = new ReviewHarness();
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m), ReviewHarness.Row(2, 700m)]);
        Evidence(h, ReviewHarness.Now.AddMinutes(-5), (4, ReconciliationStates.Passed, []), (32, ReconciliationStates.Passed, []));
        await h.RefreshAsync();
        var (summary, confirm) = await h.PrepareAsync(h.All(Current()), "held-later");
        Assert.Equal(2, summary.Count);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        // The after-refresh check of Dubai finds Customer 2's unit persistently different: the revalidation rebuilds the records with the unit held, sees a changed record, sends nothing.
        Evidence(h, ReviewHarness.Now.AddMinutes(1), (4, ReconciliationStates.Failed, [("T0002", 1002L, "TP140-2")]));
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var dispatch = (await scope.Store.ListDispatchesAsync(1, CancellationToken.None)).Single();
        Assert.Equal(DispatchStatus.ReviewRequired, dispatch.Status);
        Assert.Contains("1 approved record(s) changed", dispatch.StatusReason);
        Assert.Empty(h.Client.Calls);
    }

    [Fact]
    public void TheHeldUnitReason_IsNeedsReview_AndTheBuilderStampsOnlyThatUnit()
    {
        var rows = new[] { ReviewHarness.Row(1, 500m), ReviewHarness.Row(2, 700m) };
        var context = new ReviewBuildContext(new DateOnly(2026, 10, 14), ReviewHarness.Now, "t", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance,
            new Dictionary<string, string>(), null, null, new HashSet<(int, string, long)> { (4, "T0002", 1002L) });
        var built = MirrorCrmBuild.Build(rows, context).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(ReviewValidationStatus.Ready, built.Single(r => r.TenantId == "T0001").ValidationStatus);
        var held = built.Single(r => r.TenantId == "T0002");
        Assert.Equal(ReviewValidationStatus.NeedsReview, held.ValidationStatus);
        Assert.Contains(ReviewReasons.SourceMismatchUnresolved, held.Reasons);
        Assert.Equal(ReasonKind.NeedsReview, ReviewReasons.KindOf(ReviewReasons.SourceMismatchUnresolved));
    }
}
