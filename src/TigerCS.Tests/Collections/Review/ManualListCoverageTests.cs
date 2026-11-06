using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Manual amounts come from the stored approval data, which holds the instalments due from the configured PACT start date on. A From date before that cannot be calculated from it: it is
/// refused everywhere an amount is shown or exported, instead of quietly counting less. The review file lists the whole list once, from one calculation, not page by page.
/// </summary>
public sealed class ManualListCoverageTests
{
    private static async Task<ReviewHarness> SeededAsync(int customers)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        h.Source.Items.AddRange(Enumerable.Range(1, customers).Select(n => ReviewHarness.Row(n)));
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual(DateOnly? from = null) => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m, DueFrom: from);

    [Fact]
    public async Task AFromDateBeforeTheApprovalDataStarts_IsRefused_InThePreviewTheExportAndTheReviewFile()
    {
        using var h = await SeededAsync(2);
        var earlier = Manual(new DateOnly(1999, 6, 1));                                                    // the approval data reads the whole history from 2000 on, like the preview
        using var scope = h.NewScope();
        var page = await scope.Query.QueryAsync(h.Manager, earlier, 1, 50, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, page.Outcome); Assert.Contains("holds instalments due from", page.Detail);
        var export = await scope.Export.ExportAsync(h.Manager, new SelectionRequest(earlier, ReviewQueryService.ModeAllMatching), CancellationToken.None);
        Assert.False(export.IsSuccess); Assert.Null(export.Value);
        var review = await scope.ReviewExport.ExportAsync(h.Manager, earlier, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, review.Outcome); Assert.Null(review.Value);
    }

    [Fact]
    public async Task AFromDateOnOrAfterTheStart_IsCalculatedAsBefore()
    {
        using var h = await SeededAsync(2);
        using var scope = h.NewScope();
        foreach (var from in new[] { new DateOnly(2000, 1, 1), new DateOnly(2025, 6, 1), new DateOnly(2026, 1, 1) })                   // any date inside the stored history
        {
            var filter = Manual(from);
            Assert.True((await scope.Query.QueryAsync(h.Manager, filter, 1, 50, CancellationToken.None)).IsSuccess);
            // The same customers are exported three times here; a repeat is noted on the file, never a block (GenesysExportRepeatTests).
            Assert.True((await scope.Export.ExportAsync(h.Manager, new SelectionRequest(filter, ReviewQueryService.ModeAllMatching), CancellationToken.None)).IsSuccess);
        }
    }

    [Fact]
    public async Task TheReviewFile_HoldsEveryCustomerOnce_FromOneCalculation_NotPageByPage()
    {
        using var h = await SeededAsync(150);                                                          // more than one page of 100
        using var scope = h.NewScope();
        var dto = (await scope.ReviewExport.ExportAsync(h.Manager, Manual(), CancellationToken.None)).Value!;
        var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal(150, dto.RowCount); Assert.Equal(151, rows.Count);
        Assert.Equal(150, rows.Skip(1).Select(r => r[0]).Distinct(StringComparer.Ordinal).Count());
    }
}
