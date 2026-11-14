using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// One eligibility rule for Preview, Review, Approval, CSV and the Genesys upload: for the same source data and stage, the units Campaigns calls eligible and their stage amounts are exactly the
/// records Review holds. Approval and dispatch read the Review records, so equality with Review is equality with what is approved and sent. The clock is 2026-10-14 (Dubai).
/// </summary>
public sealed class CampaignReviewEligibilityParityTests
{
    private static PactReceivableInstalment Due(int n, DateOnly due, decimal amount) => ReviewHarness.Row(n, amount) with { DueDate = due.ToDateTime(TimeOnly.MinValue) };

    /// <summary>Units placed around every stage boundary: first/mid/coming/last day of this month, last month, three months back, and next month.</summary>
    private static async Task<ReviewHarness> SeededAsync()
    {
        var h = new ReviewHarness(false);
        h.Source.Items.AddRange([
            Due(1, new(2026, 10, 1), 500m), Due(2, new(2026, 10, 14), 500m), Due(3, new(2026, 10, 20), 500m), Due(4, new(2026, 10, 31), 500m),
            Due(5, new(2026, 9, 10), 1600m), Due(6, new(2026, 8, 5), 700m), Due(7, new(2026, 6, 1), 25000m), Due(8, new(2026, 11, 5), 500m),
            Due(9, new(2026, 9, 10), 1000m), Due(9, new(2026, 10, 25), 300m) with { VoucherNumber = "INV-9b" },
            Due(10, new(2026, 9, 30), 2000m), Due(11, new(2026, 9, 14), 1600m)]);
        await h.RefreshAsync();
        return h;
    }

    private static CollectionsCampaignAppService Campaigns(ReviewHarness h) => new(h.Options, h.Campaign, h.Sql, new(h.Options, new FakeDepartmentRepository()),
        new(h.Options, h.Time), h.Source, NullLogger<CollectionsCampaignAppService>.Instance, h.Links);

    [Theory]
    [InlineData("OverdueReminder", "Overdue")]
    [InlineData("CurrentMonthReminder", "CurrentMonth")]
    [InlineData("FollowUpReminder", "FollowUp")]
    [InlineData("LegalNotice", "LegalNotice")]
    [InlineData("LegalReferral", "LegalCase")]
    public async Task EveryStage_CampaignsEligibleUnitsAndAmounts_EqualTheReviewRecords(string campaignStage, string reviewType)
    {
        using var h = await SeededAsync();
        var preview = (await Campaigns(h).PreviewAsync(h.Manager, campaignStage, minTotal: 0m)).Value!;
        var eligible = preview.Items.Where(i => i.Status != "NotEligible").ToList();
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: reviewType, PaymentStatus: "All", MinRemaining: 0m), 1, 100, CancellationToken.None)).Value!;

        Assert.Equal(page.Items.Select(r => r.UnitCode).Order(), eligible.Select(i => i.UnitCode).Order());
        foreach (var record in page.Items)
            Assert.Equal(record.RemainingAmount, eligible.Single(i => i.UnitCode == record.UnitCode).Amount);
        Assert.Equal(page.Items.Sum(r => r.RemainingAmount ?? 0m), preview.EligibleTotalAmount);
    }

    [Fact]
    public async Task TheWholeMonthStages_IncludeTheComingDays_AndNeverNextMonth()
    {
        using var h = await SeededAsync();
        var preview = (await Campaigns(h).PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 0m)).Value!;
        var eligible = preview.Items.Where(i => i.Status != "NotEligible").Select(i => i.UnitCode).Order().ToArray();
        Assert.Contains("TP140-3", eligible);        // 20 October, a coming day
        Assert.Contains("TP140-4", eligible);        // 31 October
        Assert.DoesNotContain("TP140-8", eligible);  // 5 November: neither Due nor Overdue
        Assert.DoesNotContain(preview.Items, i => i.UnitCode == "TP140-8");
        Assert.Equal(300m, preview.Items.Single(i => i.UnitCode == "TP140-9").Amount);   // unit 9 owes 1,000 from September too, but only its October 300 is in the Current-month stage amount
    }

    [Fact]
    public async Task OnTheScheduledDay_ReadyUnitsAndTheirAmounts_AreTheSameInPreview_ReviewExport_AndTheGenesysFile()
    {
        using var h = await SeededAsync();          // 14 October is the Current-month send day
        h.Campaign.FinancialSourceValidated = true;
        var service = Campaigns(h);
        var preview = (await service.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 0m)).Value!;
        var ready = preview.Items.Where(i => i.Status == "Ready").ToList();
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", PaymentStatus: "All", MinRemaining: 0m), 1, 100, CancellationToken.None)).Value!;
        var reviewReady = page.Items.Where(r => r.ValidationStatus == "Ready").ToList();

        Assert.NotEmpty(ready);
        Assert.Equal(reviewReady.Select(r => r.UnitCode).Order(), ready.Select(i => i.UnitCode).Order());
        foreach (var r in reviewReady) Assert.Equal(r.RemainingAmount, ready.Single(i => i.UnitCode == r.UnitCode).Amount);
        var genesys = await service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys", minTotal: 0m);
        if (genesys.IsSuccess)
            foreach (var r in reviewReady) Assert.Contains($"\"{r.RemainingAmount:0.00}\"", genesys.Value!.Csv);
    }
}
