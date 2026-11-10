using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.Collections.Snapshot;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Manual list and the Auto preview, given identical filters, hold the same financially qualifying customers with the same amounts. Manual waives only the stage schedule: a customer the Auto
/// preview shows as "Preview Only" (eligible, not today's scheduled day) is Ready in Manual unless another rule holds it back, and a customer another rule holds back stays held back, visible with
/// that exact reason. The harness clock is 15 Oct 2026, not a scheduled day for the Current Month reminder.
/// </summary>
public sealed class ManualAutoParityTests
{
    private static SnapshotStatusDto Status(DateTime now) =>
        ReceivablesSnapshotComposer.BuildStatus(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31),
            new[] { FakeSnapshotSource.Healthy(4, now.AddMinutes(-5)), FakeSnapshotSource.Healthy(32, now.AddMinutes(-5)) }, new List<UnmatchedTowerDto>(), now, 90);

    /// <summary>
    /// Three customers: one whose remaining amount the source contradicts (a remainder above the original amount, so its status is Unknown), one whose two instalments
    /// are each below 100 but add up to 130, and one plain customer. The fake source honours the per-instalment minimum it is asked for, as PACT does.
    /// </summary>
    private static ReviewHarness Harness()
    {
        var h = new ReviewHarness();
        h.Source.HonourMinAmount = true;
        h.Source.Snapshot = Status(ReviewHarness.Now);
        h.Source.Items.AddRange(
        [
            ReviewHarness.Row(1, 500m, day: 20, status: "", plan: 400m) with { AllocatedAmount = 0m },
            ReviewHarness.Row(2, 60m, day: 10) with { VoucherNumber = "INV-2A" }, ReviewHarness.Row(2, 70m, day: 20) with { VoucherNumber = "INV-2B" },
            ReviewHarness.Row(3, 250.5m, day: 10)
        ]);
        return h;
    }

    private static CollectionsCampaignAppService Preview(ReviewHarness h) =>
        new(h.Options, h.Campaign, h.Sql, new(h.Options, new TigerCS.Tests.IdentityAndAccess.Fakes.FakeDepartmentRepository()),
            new(h.Options, h.Time), h.Source, Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsCampaignAppService>.Instance);

    private static ReviewFilter Manual(decimal minTotal = 100m) => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: minTotal);

    [Fact]
    public async Task WithIdenticalFilters_ManualListsTheSameCustomersAsTheAutoPreview_WithTheSameAmounts()
    {
        using var h = Harness();
        var auto = (await Preview(h).PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 100m)).Value!;
        Assert.Equal(3, auto.TotalCount);
        Assert.All(auto.Items, i => Assert.Contains(i.Status, new[] { "Ready", "PreviewOnly" }));         // financially eligible, whether or not today is the scheduled day

        await h.RefreshAsync();
        using var scope = h.NewScope();
        var manual = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
        Assert.Equal(auto.TotalCount, manual.TotalCount);                                                 // not "No customers match these filters"
        Assert.Equal(2, manual.Counts.Ready);                                                             // the schedule is waived; only the unconfirmed payment status holds one back
        Assert.Equal(auto.Items.OrderBy(i => i.UnitCode).Select(i => (i.UnitCode, i.Amount)),
                     manual.Items.OrderBy(i => i.UnitCode).Select(i => (i.UnitCode, i.RemainingAmount)));   // unit by unit, the same amount
    }

    [Fact]
    public async Task ACustomerWhoseInstalmentsAreEachBelowTheMinimum_ButWhoseTotalIsAbove_IsInBothLists_WithTheWholeTotal()
    {
        using var h = Harness();
        var auto = (await Preview(h).PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 100m)).Value!;
        Assert.Equal(130m, auto.Items.Single(i => i.UnitCode == "TP140-2").Amount);
        await h.RefreshAsync();
        Assert.All(h.Source.Requests, r => Assert.Equal(0m, r.MinAmount));                                // the refresh reads every instalment, as the preview does
        using var scope = h.NewScope();
        var manual = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
        var unit = manual.Items.Single(i => i.UnitCode == "TP140-2");
        Assert.Equal(130m, unit.RemainingAmount); Assert.Equal("Ready", unit.ValidationStatus);           // Preview Only in Auto becomes Ready in Manual
    }

    [Fact]
    public async Task AnUnconfirmedPaymentStatus_IsListedAndHeldBackWithItsReason_NeverHidden()
    {
        using var h = Harness();
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var manual = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
        var unknown = manual.Items.Single(i => i.UnitCode == "TP140-1");
        Assert.Equal("Unknown", unknown.PaymentStatus); Assert.Equal("NeedsReview", unknown.ValidationStatus);
        Assert.Contains(unknown.Reasons, r => r.Code == ReviewReasons.PaymentStatusUnknown);            // the exact reason, for the review workflow
        Assert.Equal(2, manual.Counts.Ready); Assert.Equal(3, manual.Counts.Total);
        // Choosing a status explicitly still narrows the list, as before.
        var unpaidOnly = (await scope.Query.QueryAsync(h.Manager, Manual() with { PaymentStatus = "Unpaid" }, 1, 100, CancellationToken.None)).Value!;
        Assert.Equal(2, unpaidOnly.TotalCount); Assert.DoesNotContain(unpaidOnly.Items, i => i.UnitCode == "TP140-1");
    }

    [Fact]
    public async Task OlderArrears_CountInBothModes_SoDueOverdueAndTotalAgree()
    {
        // A customer with a 2025 instalment still unpaid: the preview's default period is the whole history, and so is the stored approval data. Both = Total, Due and Overdue each match.
        using var h = Harness();
        h.Source.Items.AddRange([ReviewHarness.Row(4, 300m, day: 10) with { DueDate = new DateTime(2025, 8, 10), VoucherNumber = "INV-4-OLD" }, ReviewHarness.Row(4, 200m, day: 20)]);
        var auto = (await Preview(h).PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 100m)).Value!.Items.Single(i => i.UnitCode == "TP140-4");
        Assert.Equal((500m, 200m, 300m), (auto.TotalAmount, auto.DueAmount, auto.OverdueAmount));
        await h.RefreshAsync();
        using var scope = h.NewScope();
        decimal Amount(string? dueStatus) => (scope.Query.QueryAsync(h.Manager, Manual() with { DueStatus = dueStatus }, 1, 100, CancellationToken.None).GetAwaiter().GetResult()).Value!
            .Items.Single(i => i.UnitCode == "TP140-4").RemainingAmount!.Value;
        Assert.Equal(auto.TotalAmount, Amount(null)); Assert.Equal(auto.DueAmount, Amount("Due")); Assert.Equal(auto.OverdueAmount, Amount("Overdue"));
    }

    [Fact]
    public async Task ATowerFilter_ListsTheSameCustomersInBothModes()
    {
        using var h = Harness();
        h.Source.Items.Add(ReviewHarness.Row(5, 400m, day: 20) with { TowerId = 7, TowerNumber = "124", TowerName = "Tower 124" });
        var auto = (await Preview(h).PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 100m, towerId: 7)).Value!;
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var manual = (await scope.Query.QueryAsync(h.Manager, Manual() with { TowerId = 7 }, 1, 100, CancellationToken.None)).Value!;
        Assert.Equal(["TP140-5"], auto.Items.Select(i => i.UnitCode)); Assert.Equal(["TP140-5"], manual.Items.Select(i => i.UnitCode));
        Assert.False(manual.TowerDataMissing);
    }

    [Fact]
    public async Task SearchingByUnitCodeOrEmail_FindsTheSameCustomerInBothModes()
    {
        using var h = Harness();
        await h.RefreshAsync();
        using var scope = h.NewScope();
        foreach (var term in new[] { "TP140-3", "c3@example.test" })
        {
            var auto = (await Preview(h).PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 100m, search: term)).Value!;
            var manual = (await scope.Query.QueryAsync(h.Manager, Manual() with { Customer = term }, 1, 100, CancellationToken.None)).Value!;
            Assert.Equal(["TP140-3"], auto.Items.Select(i => i.UnitCode)); Assert.Equal(["TP140-3"], manual.Items.Select(i => i.UnitCode));
        }
    }

    [Fact]
    public async Task TheExportHoldsTheWholeManualPopulation_HeldBackCustomerIncludedAndSaidSo_WhileTheSendStillLeavesHimOut()
    {
        using var h = Harness();
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var export = (await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
        var rows = GenesysContactTemplate.ParseCsv(export.Csv);
        Assert.Equal(3, export.RowCount);
        Assert.Contains(rows.Skip(1), r => ManualGenesysFile.Cell(rows, r, "CustomerName") == "Customer 1" && ManualGenesysFile.Cell(rows, r, "AmountDue") == "500.00");                   // held back from sending (unconfirmed payment status), still in the file, as read
        Assert.Contains(rows.Skip(1), r => ManualGenesysFile.Cell(rows, r, "CustomerName") == "Customer 2" && ManualGenesysFile.Cell(rows, r, "AmountDue") == "130.00");
        Assert.Equal(1, export.IncludedByStatus!["NeedsReview"]); Assert.False(export.DataVerified); Assert.Contains("PaymentStatusUnknown:1", export.UnverifiedReasons!);
        // The send population is the Ready customers only: the same filters, without customer 1.
        var send = (await scope.Query.SummarizeAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
        Assert.Equal(2, send.Count); Assert.DoesNotContain(send.Contacts, c => c.CustomerName == "Customer 1");
    }
}
