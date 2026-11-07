using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.Collections.Crm;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The confirmed business decision: every property unit is its own Genesys contact record and receives its own call, even when several units share a phone number. A phone number is
/// never an identifier of a customer or a unit. Every other send protection (reconciliation, phone validity, contact conflicts, unit-level duplicate prevention, idempotency) stays exactly
/// as it is, and Manual and Auto decide the same way. The harness clock is 14 Oct 2026, a scheduled day for the Current Month reminder.
/// </summary>
public sealed class PerUnitCallsTests
{
    private const string Phone = "971500007777";

    /// <summary>One customer (tenant T0001) with three units on one number, each with its own balance and due date.</summary>
    private static PactReceivableInstalment[] ThreeUnitsOneCustomer() =>
    [
        ReviewHarness.Row(1, 300m, day: 16, mobile: Phone),
        ReviewHarness.Row(1, 450m, day: 18, mobile: Phone) with { UnitId = 2001, UnitCode = "TP140-2001", VoucherNumber = "INV-1-B" },
        ReviewHarness.Row(1, 725m, day: 20, mobile: Phone) with { UnitId = 2002, UnitCode = "TP140-2002", VoucherNumber = "INV-1-C" }
    ];

    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);
    private static ReviewFilter Auto() => new(ReminderType: "CurrentMonth", SendingMode: "Auto");

    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : ThreeUnitsOneCustomer());
        await h.RefreshAsync();
        return h;
    }

    private static async Task<List<CollectionsDispatchItem>> ItemsAsync(ReviewHarness h)
    {
        using var context = h.CreateContext();
        return await context.CollectionsDispatchItems.AsNoTracking().OrderBy(i => i.UnitCode).ToListAsync();
    }

    // 1, 3, 4: same customer, three units, one number -> three independent records with their own amount and due date, all sent

    [Fact]
    public async Task SameCustomer_ThreeUnits_SamePhone_AreThreeIndependentlyEligibleRecords_EachWithItsOwnAmountAndDueDate()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        foreach (var filter in new[] { Manual(), Auto() })
        {
            var page = (await scope.Query.QueryAsync(h.Manager, filter, 1, 50, CancellationToken.None)).Value!;
            Assert.Equal(3, page.Counts.Ready);
            Assert.All(page.Items, i => { Assert.Equal("Ready", i.ValidationStatus); Assert.Empty(i.Reasons); Assert.Equal("+" + Phone, i.Phone); });
            Assert.Equal([300m, 450m, 725m], page.Items.Select(i => i.RemainingAmount!.Value).Order());
            Assert.Equal([new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 20)], page.Items.Select(i => i.DueDate!.Value).Order());
        }
        var (summary, confirm) = await h.PrepareAsync(h.All(Manual()), "three");
        Assert.Equal(3, summary.Count); Assert.Equal((3, 1), (summary.SharedPhoneRecordCount, summary.SharedPhoneNumberCount));
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var contacts = h.Client.Calls.Single().Contacts;
        Assert.Equal(3, contacts.Count);
        Assert.All(contacts, c => Assert.Equal("+" + Phone, c.Phone));
        Assert.Equal(["300.00", "450.00", "725.00"], contacts.Select(c => c.AmountDue).Order());                    // each unit's own amount, never summed
        Assert.Equal(["2026-10-16", "2026-10-18", "2026-10-20"], contacts.Select(c => c.DueDate).Order());         // each unit's own due date, never the earliest
        Assert.All(contacts, c => Assert.Equal("Current Month", c.ReminderType));
        // Every dispatch item keeps its own identity.
        var items = await ItemsAsync(h);
        Assert.Equal(["TP140-1", "TP140-2001", "TP140-2002"], items.Select(i => i.UnitCode));
        Assert.Equal(3, items.Select(i => i.RecordKey).Distinct().Count()); Assert.Equal(3, items.Select(i => i.UnitId).Distinct().Count());
        Assert.All(items, i => { Assert.Equal((4, "T0001", "Customer 1"), (i.CompanyId, i.TenantId, i.CustomerName)); Assert.Equal(DispatchItemStatus.UploadedToGenesys, i.Status); });
    }

    // 2: different customers, one number -> separate records, both sent

    [Fact]
    public async Task DifferentCustomers_SamePhone_StaySeparateRecords_AndBothAreSent()
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1, 300m, mobile: Phone), ReviewHarness.Row(2, 450m, mobile: Phone));
        var (summary, confirm) = await h.PrepareAsync(h.All(Manual()), "two-customers");
        Assert.Equal(2, summary.Count);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var contacts = h.Client.Calls.Single().Contacts;
        Assert.Equal([("Customer 1", "300.00"), ("Customer 2", "450.00")], contacts.Select(c => (c.CustomerName, c.AmountDue)).Order());
        Assert.Equal(["T0001", "T0002"], (await ItemsAsync(h)).Select(i => i.TenantId).Order());
    }

    // 5, 6: unit-level idempotency

    [Fact]
    public async Task ReprocessingTheSameUnit_IsCaughtByTheUnitLevelProtections_AndAnotherUnitOnTheSameNumberIsNot()
    {
        using var h = await SeededAsync();
        // Approve only the first unit, by its unit key.
        var one = new SelectionRequest(Manual(), ReviewQueryService.ModeSelectedUnits, null, null, 1, 50, [ReviewQueryService.UnitKey(4, "T0001", "TP140-1")]);
        var (first, confirm) = await h.PrepareAsync(one, "unit-1");
        Assert.Equal(1, first.Count);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        // The same request again: the idempotency key replays the same dispatch; a different list under that key is a conflict.
        Assert.Equal(CollectionsOutcome.Replayed, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        var (_, other) = await h.PrepareAsync(h.All(Manual()), "unit-1");
        Assert.Equal(CollectionsOutcome.IdempotencyConflict, (await scope.Dispatch.ConfirmAsync(h.Manager, other, CancellationToken.None)).Outcome);
        // The unit itself is now "already sent" for the day (live record + contact-frequency limit, both keyed by the unit, never by the number) ...
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal("AlreadySent", page.Items.Single(i => i.UnitCode == "TP140-1").ValidationStatus);
        // ... while the two other units on the SAME number are still Ready and can be approved and sent on their own.
        Assert.All(page.Items.Where(i => i.UnitCode != "TP140-1"), i => Assert.Equal("Ready", i.ValidationStatus));
        var (rest, confirmRest) = await h.PrepareAsync(h.All(Manual()), "units-2-3");
        Assert.Equal(2, rest.Count);
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirmRest, CancellationToken.None)).Outcome);
        foreach (var id in h.Scheduler.Dispatches) await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.Equal(3, h.Client.Calls.Sum(c => c.Contacts.Count));
        Assert.Equal(["TP140-1", "TP140-2001", "TP140-2002"], (await ItemsAsync(h)).Select(i => i.UnitCode));   // one live item per unit: the unique index holds
    }

    // 7: an invalid number is still blocked (and does not block its neighbours on a valid one)

    [Fact]
    public async Task AnInvalidPhone_IsStillBlocked()
    {
        var rows = ThreeUnitsOneCustomer();
        rows[2] = rows[2] with { Mobile = "123" };
        using var h = await SeededAsync(null, rows);
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        var bad = page.Items.Single(i => i.UnitCode == "TP140-2002");
        Assert.Equal("NeedsReview", bad.ValidationStatus); Assert.Contains(bad.Reasons, r => r.Code == ReviewReasons.NoValidContact);
        Assert.Equal(2, page.Counts.Ready);
        Assert.Equal(2, (await h.PrepareAsync(h.All(Manual()), "invalid")).Summary.Count);
    }

    // 8: the reconciliation gate still blocks the send

    [Fact]
    public async Task WhenTheFinancialSourceIsNotReconciled_SendIsStillBlocked_ForEveryUnit()
    {
        using var h = await SeededAsync(x => x.Campaign.FinancialSourceValidated = false);
        var (summary, confirm) = await h.PrepareAsync(h.All(Manual()), "unreconciled");
        Assert.Equal(3, summary.Count); Assert.False(summary.SourceReconciled);
        using var scope = h.NewScope();
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("not reconciled", refused.Detail);
        Assert.Empty(h.Scheduler.Dispatches); Assert.Empty(h.Client.Calls);
        h.Campaign.AutoSendingEnabled = true;
        Assert.Equal("NotReconciled", (await scope.Auto.RunAsync(CancellationToken.None)).Stages.Single(s => s.Stage == "CurrentMonth").Outcome);
    }

    // 9: a contact source conflict still holds the unit, and only that unit

    [Fact]
    public async Task AContactSourceConflict_StillHoldsThatUnit_WhileTheOtherUnitsOnTheNumberAreSent()
    {
        var store = new FakeCrmOwnerStore { LastSuccessUtc = ReviewHarness.Now.AddMinutes(-20) };
        await store.ReplaceAsync([new CrmOwnerRow("TP140-2001", "TP140", "1", 10, 8, 1, 5001, 7, "Crm Customer 1", "+966508650505", "c1@example.test", "+966508650505", "c1@example.test", "Blue Wave")], default, null);
        var links = new CollectionsUnitLinkService(store, new CollectionsCrmOwnersOptions { Enabled = true }, new FakeTimeProvider(ReviewHarness.Now), NullLogger<CollectionsUnitLinkService>.Instance);
        using var h = await SeededAsync(x => x.Links = links);
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        var conflict = page.Items.Single(i => i.UnitCode == "TP140-2001");
        Assert.Equal("NeedsReview", conflict.ValidationStatus); Assert.Contains(conflict.Reasons, r => r.Code == ReviewReasons.ContactSourceConflict);
        Assert.Equal(2, page.Counts.Ready);
        var (summary, confirm) = await h.PrepareAsync(h.All(Manual()), "conflict");
        Assert.Equal(2, summary.Count);
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal(["300.00", "725.00"], h.Client.Calls.Single().Contacts.Select(c => c.AmountDue).Order());
    }

    // 10: Manual and Auto decide the same way

    [Fact]
    public async Task ManualAndAuto_AreConsistent_ThreeUnitsOnOneNumber_AreThreeCallsInBothModes()
    {
        using var auto = await SeededAsync(x => x.Campaign.AutoSendingEnabled = true);
        using (var scope = auto.NewScope())
        {
            var stage = (await scope.Auto.RunAsync(CancellationToken.None)).Stages.Single(s => s.Stage == "CurrentMonth");
            Assert.Equal(("Dispatched", 3), (stage.Outcome, stage.Count));
            await scope.Dispatch.ExecuteAsync(auto.Scheduler.Dispatches.Single(), CancellationToken.None);
            Assert.Equal(["300.00", "450.00", "725.00"], auto.Client.Calls.Single().Contacts.Select(c => c.AmountDue).Order());
            Assert.Equal(3, (await ItemsAsync(auto)).Count);
        }
        using var manual = await SeededAsync();
        var (summary, confirm) = await manual.PrepareAsync(manual.All(Manual()), "manual");
        using (var scope = manual.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(manual.Manager, confirm, CancellationToken.None)).Outcome);
            await scope.Dispatch.ExecuteAsync(manual.Scheduler.Dispatches.Single(), CancellationToken.None);
        }
        Assert.Equal(auto.Client.Calls.Single().Contacts.Select(c => (c.Phone, c.CustomerName, c.AmountDue, c.DueDate)).Order(),
            manual.Client.Calls.Single().Contacts.Select(c => (c.Phone, c.CustomerName, c.AmountDue, c.DueDate)).Order());
    }

    // 11: the export is unchanged: still one row per unit

    [Fact]
    public async Task TheGenesysExport_IsUnchanged_OneRowPerUnit()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var dto = (await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
        var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal(3, dto.RowCount); Assert.Empty(dto.Exclusions!);
        Assert.Equal(["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], rows[0]);
        Assert.Equal(["300.00", "450.00", "725.00"], rows.Skip(1).Select(r => r[4]).Order());
        Assert.All(rows.Skip(1), r => Assert.Equal("+" + Phone, r[0]));
    }

    [Fact]
    public void ARepeatedNumberAlone_NeverProducesAHold_InTheBuilder()
    {
        var context = new ReviewBuildContext(new DateOnly(2026, 10, 14), ReviewHarness.Now, "t", "AED", true, true, false, 0.0001m, new Dictionary<string, string>());
        var records = ReviewRecordBuilder.Build(ThreeUnitsOneCustomer().Concat([ReviewHarness.Row(9, 100m, mobile: Phone)]).ToList(), context).Records
            .Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(4, records.Count);
        Assert.All(records, r => { Assert.Equal(ReviewValidationStatus.Ready, r.ValidationStatus); Assert.Equal("", r.Reasons); });
        // The per-unit hold is untouched: a unit scheduled for two reminder types today is still held (day 14: Current Month and Legal Notice).
        var september = ReviewHarness.Row(5, 1600m) with { DueDate = new DateTime(2026, 9, 10), VoucherNumber = "INV-SEP" };
        var overlap = ReviewRecordBuilder.Build([september, ReviewHarness.Row(5, 400m, day: 20)], context).Records.Where(r => r.ReminderType is CampaignReminderType.CurrentMonth or CampaignReminderType.LegalNotice).ToList();
        Assert.All(overlap, r => Assert.Contains(ReviewReasons.ReminderTypeOverlap, ReviewRecordBuilder.Parse(r.Reasons)));
    }
}
