using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Manual summary cards (Customers, Eligible, Held back, Already sent) narrow the rows of the list through the review filter's <c>ValidationStatus</c>. "HeldBack" is the Needs review + Excluded set the
/// Held back tile counts. The narrowing never changes the counts, the eligible amount or what a selection resolves: it only decides which rows are listed.
/// </summary>
public sealed class ManualCardFilterQueryTests
{
    /// <summary>
    /// 15 Oct 2026. Customer 1: an unconfirmed payment status (held back, Needs review). Customers 2 and 3 are put in a Manual dispatch (Already sent). Customers 4 and 5 arrive afterwards (Eligible).
    /// </summary>
    private static async Task<ReviewHarness> ScenarioAsync()
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m, day: 20, status: "", plan: 400m) with { AllocatedAmount = 0m }, ReviewHarness.Row(2), ReviewHarness.Row(3)]);
        await h.RefreshAsync();
        var (_, confirm) = await h.PrepareAsync(h.All(Manual()), "cards");
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        h.Source.Items.AddRange([ReviewHarness.Row(4), ReviewHarness.Row(5)]);
        h.Time.Advance(TimeSpan.FromMinutes(5)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1).AddMinutes(5);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual(string? status = null) => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m, ValidationStatus: status);

    private static async Task<ReviewPageDto> ListAsync(ReviewHarness h, string? status, int page = 1, int size = 100)
    {
        using var scope = h.NewScope();
        var result = await scope.Query.QueryAsync(h.Manager, Manual(status), page, size, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Detail);
        return result.Value!;
    }

    private static List<string> Units(ReviewPageDto page) => [.. page.Items.Select(i => i.UnitCode)];

    [Fact]
    public async Task TheScenario_HasOneHeldBack_TwoAlreadySent_AndTwoEligibleCustomers()
    {
        using var h = await ScenarioAsync();
        var all = await ListAsync(h, null);
        Assert.Equal((5, 2, 1, 0, 2), (all.Counts.Total, all.Counts.Ready, all.Counts.NeedsReview, all.Counts.Excluded, all.Counts.AlreadySent));
        Assert.Equal(5, all.TotalCount);
    }

    [Theory]
    [InlineData(null, new[] { "TP140-1", "TP140-2", "TP140-3", "TP140-4", "TP140-5" })]      // Customers: everyone
    [InlineData("All", new[] { "TP140-1", "TP140-2", "TP140-3", "TP140-4", "TP140-5" })]
    [InlineData("Ready", new[] { "TP140-4", "TP140-5" })]                                      // Eligible
    [InlineData("HeldBack", new[] { "TP140-1" })]                                              // Held back
    [InlineData("Held back", new[] { "TP140-1" })]
    [InlineData("heldback", new[] { "TP140-1" })]
    [InlineData("AlreadySent", new[] { "TP140-2", "TP140-3" })]                                // Already sent
    public async Task EachCard_ListsExactlyItsCustomers(string? status, string[] expected)
    {
        using var h = await ScenarioAsync();
        var page = await ListAsync(h, status);
        Assert.Equal(expected, Units(page));
        Assert.Equal(expected.Length, page.TotalCount);                                          // the paging total follows the card
    }

    [Fact]
    public async Task TheListMatchesTheSummaryCounts_ForEveryCard()
    {
        using var h = await ScenarioAsync();
        var counts = (await ListAsync(h, null)).Counts;
        Assert.Equal(counts.Total, (await ListAsync(h, null)).TotalCount);
        Assert.Equal(counts.Ready, (await ListAsync(h, "Ready")).TotalCount);
        Assert.Equal(counts.NeedsReview + counts.Excluded, (await ListAsync(h, "HeldBack")).TotalCount);
        Assert.Equal(counts.AlreadySent, (await ListAsync(h, "AlreadySent")).TotalCount);
        Assert.Equal(counts.Total, counts.Ready + counts.NeedsReview + counts.Excluded + counts.AlreadySent);
    }

    [Fact]
    public async Task TheCountsAndTheEligibleAmount_NeverChangeWithTheCard()
    {
        using var h = await ScenarioAsync();
        var everything = await ListAsync(h, null);
        foreach (var status in new[] { "Ready", "HeldBack", "AlreadySent", "NeedsReview", "Excluded" })
        {
            var page = await ListAsync(h, status);
            Assert.Equal(everything.Counts, page.Counts);
            Assert.Equal(everything.ReadyTotalAmount, page.ReadyTotalAmount);
        }
        Assert.Equal(1000m, everything.ReadyTotalAmount);                                          // customers 4 and 5, 500 each
    }

    [Fact]
    public async Task HeldBackRows_CarryTheirReason()
    {
        using var h = await ScenarioAsync();
        var held = Assert.Single((await ListAsync(h, "HeldBack")).Items);
        Assert.Equal("NeedsReview", held.ValidationStatus);
        Assert.Contains(held.Reasons, r => r.Code == ReviewReasons.PaymentStatusUnknown && r.Explanation.Length > 0);
    }

    [Fact]
    public async Task HeldBack_IsOnlyAvailableInManual()
    {
        using var h = await ScenarioAsync();
        using var scope = h.NewScope();
        var auto = await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", ValidationStatus: "HeldBack"), 1, 25, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, auto.Outcome);
        Assert.Contains("Manual", auto.Detail);
        var unknown = await scope.Query.QueryAsync(h.Manager, Manual("Nonsense"), 1, 25, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, unknown.Outcome);
        Assert.Contains("HeldBack", unknown.Detail);
    }

    [Fact]
    public async Task TheCard_DoesNotChangeWhatASelectionResolves_NorWhatCanBeSent()
    {
        using var h = await ScenarioAsync();
        async Task<SelectionSummaryDto> Summary(string? status)
        {
            using var scope = h.NewScope();
            return (await scope.Query.SummarizeAsync(h.Manager, h.All(Manual(status)), CancellationToken.None)).Value!;
        }
        var plain = await Summary(null);
        Assert.Equal(2, plain.Count);                                                              // only the eligible customers (4 and 5) are ever sent
        foreach (var status in new[] { "Ready", "HeldBack", "AlreadySent" })
        {
            var narrowed = await Summary(status);
            Assert.Equal(plain.Count, narrowed.Count); Assert.Equal(plain.Fingerprint, narrowed.Fingerprint);
        }
    }

    [Fact]
    public async Task Paging_FollowsTheCard()
    {
        using var h = await ScenarioAsync();
        var first = await ListAsync(h, "Ready", page: 1, size: 1);
        var second = await ListAsync(h, "Ready", page: 2, size: 1);
        Assert.Equal(["TP140-4"], Units(first)); Assert.Equal(["TP140-5"], Units(second));
        Assert.Equal(2, first.TotalCount); Assert.Equal(2, second.TotalCount);
    }
}
