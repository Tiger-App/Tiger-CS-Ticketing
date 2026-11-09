using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Guards on the Manual path that the export and the send rely on: a record-key list cannot skip the Manual filter, an unresolved amount is never rounded into a Ready one, Manual lists are not
/// narrowed by month, a person cannot confirm an Auto selection, the source switch stops a file, and a list larger than one pass can read is refused rather than truncated.
/// </summary>
public sealed class ManualGuardsTests
{
    private static async Task<ReviewHarness> SeededAsync(params TigerCS.Application.Modules.Collections.Abstractions.PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : [ReviewHarness.Row(1, 500m, day: 20), ReviewHarness.Row(2, 900m, day: 20)]);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);

    [Fact]
    public async Task ARecordKeyList_CannotCarryAManualFilter_BecauseItWouldSkipTheFilter()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var keys = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!.Items.Select(i => i.RecordKey).ToList();
        // A Minimum Total of 100,000 leaves nobody, so the list must not fall back to the stored stage amounts.
        var export = await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual() with { MinRemaining = 100_000m }, ReviewQueryService.ModeSelectedKeys, keys), CancellationToken.None);
        Assert.False(export.IsSuccess); Assert.Null(export.Value); Assert.Contains("Manual", export.Detail);
    }

    [Fact]
    public async Task AUnitWithAnAmountThatCannotBeStated_IsHeldBack_AndNeverRoundedIntoAFile()
    {
        // 50.125 is a normal balance with extra decimals and is simply rounded (50.13). A POSITIVE residue below 0.005 would round to a settled 0.00, so it is never rounded away: it is an OVERDUE
        // instalment outside the current-month stage's own rows, and the unit is still held, not summed with its clean 500.00 into a sendable amount.
        using var rounded = await SeededAsync(ReviewHarness.Row(1, 500m, day: 20), ReviewHarness.Row(1, 50.125m, day: 10) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1-X" });
        using (var roundedScope = rounded.NewScope())
        {
            var okPage = (await roundedScope.Query.QueryAsync(rounded.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
            Assert.DoesNotContain(okPage.Items, i => i.Reasons.Any(r => r.Code == ReviewReasons.AmountPrecisionNeedsReview));
        }
        using var h = await SeededAsync(ReviewHarness.Row(1, 500m, day: 20), ReviewHarness.Row(1, 0.004m, day: 10) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1-X" });
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(0, page.Counts.Ready);
        Assert.All(page.Items, i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.AmountPrecisionNeedsReview));
        var export = await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None);
        Assert.False(export.IsSuccess); Assert.Null(export.Value);
    }

    [Fact]
    public async Task AManualList_IsNotNarrowedByMonth_SoAMonthIsRefused()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var page = await scope.Query.QueryAsync(h.Manager, Manual() with { Year = 2026, Month = 10 }, 1, 50, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, page.Outcome); Assert.Contains("From and To", page.Detail);
    }

    [Fact]
    public async Task WhenTheFinancialSourceIsSwitchedOff_NoFileIsReleased_EvenFromStoredData()
    {
        using var h = await SeededAsync();                                                     // the approval data was built while the source was on
        h.Sql.Enabled = false;
        using var scope = h.NewScope();
        var export = await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Disabled, export.Outcome); Assert.Null(export.Value);
        var review = await scope.ReviewExport.ExportAsync(h.Manager, Manual(), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Disabled, review.Outcome); Assert.Null(review.Value);
    }

    [Fact]
    public async Task AListLargerThanOnePass_IsRefused_NotTruncated()
    {
        using var h = await SeededAsync();                                                     // two customers
        h.Review.ManualListCapacity = 1;
        using var scope = h.NewScope();
        var page = await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, page.Outcome); Assert.Contains("more customers", page.Detail);
        var export = await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None);
        Assert.False(export.IsSuccess); Assert.Null(export.Value);
        h.Review.ManualListCapacity = 2;                                                       // exactly the capacity is fine
        Assert.True((await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).IsSuccess);
    }
}
