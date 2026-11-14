using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Manual summary cards filter the list through the review service: the rows a card shows are exactly the customers its count says, on top of every other filter, and the counts never move.
/// </summary>
public sealed class ManualCardFilterTests
{
    private static async Task<ReviewHarness> SeededAsync()
    {
        var h = new ReviewHarness();
        h.Source.Items.AddRange([
            ReviewHarness.Row(1, 500m),
            ReviewHarness.Row(2, 700m),
            ReviewHarness.Row(3, 900m, name: "Zainab Al Ali"),
            ReviewHarness.Row(5, 600m, mobile: ""),   // no callable phone: held back
        ]);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual(string? status = null) => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m, ValidationStatus: status);

    private static async Task<ReviewPageDto> Page(ReviewHarness h, ReviewFilter filter, int page = 1, int size = 25)
    {
        using var scope = h.NewScope();
        var result = await scope.Query.QueryAsync(h.Manager, filter, page, size, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Detail);
        return result.Value!;
    }

    [Fact]
    public async Task Customers_ShowsEveryone_AndTheCardsAddUp()
    {
        using var h = await SeededAsync();
        var all = await Page(h, Manual());
        Assert.Equal(all.Counts.Total, all.TotalCount);
        Assert.Equal(all.Counts.Total, all.Counts.Ready + all.Counts.NeedsReview + all.Counts.Excluded + all.Counts.AlreadySent);
    }

    [Fact]
    public async Task Eligible_ShowsOnlyReadyCustomers_MatchingItsCount()
    {
        using var h = await SeededAsync();
        var all = await Page(h, Manual());
        var eligible = await Page(h, Manual("Ready"));
        Assert.Equal(all.Counts.Ready, eligible.TotalCount);
        Assert.NotEmpty(eligible.Items);
        Assert.All(eligible.Items, i => Assert.Equal("Ready", i.ValidationStatus));
        Assert.Equal(all.Counts, eligible.Counts);                       // switching card never changes the summary
    }

    [Fact]
    public async Task HeldBack_ShowsExcludedAndNeedsReview_WithTheirReasons_MatchingItsCount()
    {
        using var h = await SeededAsync();
        var all = await Page(h, Manual());
        var held = await Page(h, Manual("HeldBack"));
        Assert.Equal(all.Counts.NeedsReview + all.Counts.Excluded, held.TotalCount);       // the number on the Held back card
        Assert.NotEmpty(held.Items);
        Assert.All(held.Items, i => { Assert.Contains(i.ValidationStatus, new[] { "Excluded", "NeedsReview" }); Assert.NotEmpty(i.Reasons); });
        Assert.Equal(all.Counts, held.Counts);
    }

    [Fact]
    public async Task AlreadySent_ShowsOnlyThoseCustomers_MatchingItsCount()
    {
        using var h = await SeededAsync();
        var all = await Page(h, Manual());
        var sent = await Page(h, Manual("AlreadySent"));
        Assert.Equal(all.Counts.AlreadySent, sent.TotalCount);
        Assert.All(sent.Items, i => Assert.Equal("AlreadySent", i.ValidationStatus));
    }

    [Fact]
    public async Task EachCard_KeepsOtherFilters_AndPagesFromTheFilteredList()
    {
        using var h = await SeededAsync();
        var searched = await Page(h, Manual("Ready") with { Customer = "Zainab" });
        Assert.Equal(["Zainab Al Ali"], searched.Items.Select(i => i.CustomerName));
        var first = await Page(h, Manual("Ready"), page: 1, size: 1);
        var second = await Page(h, Manual("Ready"), page: 2, size: 1);
        Assert.Equal(first.TotalCount, second.TotalCount);
        Assert.NotEqual(first.Items.Single().CustomerName, second.Items.Single().CustomerName);
    }
}
