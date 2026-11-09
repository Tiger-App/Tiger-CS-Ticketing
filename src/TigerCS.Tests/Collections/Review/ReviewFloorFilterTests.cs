using System.Text.Json;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Floor filter on the stored review data that Manual lists, approvals, dispatches and the Auto approval use: the list a person previews is exactly the list that is approved and uploaded, All Floors changes
/// nothing, and the scheduled Auto job (which has no filter of its own) is untouched.
/// </summary>
public sealed class ReviewFloorFilterTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    // Customers by unit number: 101, 102 (floor 1), 201 (floor 2), 1921 (floor 19), P12 (a parking unit: no floor); tower 7 except 5101 (tower 8, unit TP141-101, floor 1).
    private static PactReceivableInstalment Seed(int n, decimal amount = 500m, string? code = null, int tower = 7) =>
        ReviewHarness.Row(n, amount) with { UnitCode = code ?? $"TP140-{n}", TowerId = tower, TowerNumber = tower == 7 ? "140" : "141", TowerName = tower == 7 ? "Al Ghaf" : "Al Sidr" };

    private static PactReceivableInstalment[] Rows() =>
    [
        Seed(101), Seed(102, 400m), Seed(201, 700m), Seed(1921, 300m), Seed(12, 90m, "TP140-P12"), Seed(5101, 250m, "TP141-101", tower: 8)
    ];

    private static async Task<ReviewHarness> SeededAsync(bool advanceDay = true)
    {
        var h = new ReviewHarness();
        if (advanceDay) { h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1); }
        h.Source.Items.AddRange(Rows());
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual(params int[] floors) =>
        new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m, Floors: floors.Length == 0 ? null : floors);

    private static ReviewFilter Auto(params int[] floors) =>
        new(ReminderType: "CurrentMonth", SendingMode: "Auto", MinRemaining: 0m, Floors: floors.Length == 0 ? null : floors);

    private static async Task<ReviewPageDto> ListAsync(ReviewHarness h, ReviewFilter filter)
    {
        using var scope = h.NewScope();
        var result = await scope.Query.QueryAsync(h.Manager, filter, 1, 100, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Detail);
        return result.Value!;
    }

    private static string[] Units(ReviewPageDto page) => page.Items.Select(i => i.UnitCode).Order().ToArray();

    // ------------------------------------------------------------ the list (Manual)

    [Fact]
    public async Task Manual_NoFloor_IsAllFloors_IdenticalToTheListWithoutTheFilter()
    {
        using var h = await SeededAsync();
        var none = await ListAsync(h, Manual());
        var empty = await ListAsync(h, Manual() with { Floors = [] });
        Assert.Equal(["TP140-101", "TP140-102", "TP140-1921", "TP140-201", "TP140-P12", "TP141-101"], Units(none));
        Assert.Equal(Units(none), Units(empty));
        Assert.Equal(none.Counts, empty.Counts);
    }

    [Fact]
    public async Task Manual_SingleAndMultipleFloors_AcrossTowers()
    {
        using var h = await SeededAsync();
        Assert.Equal(["TP140-101", "TP140-102", "TP141-101"], Units(await ListAsync(h, Manual(1))));
        Assert.Equal(["TP140-1921", "TP140-201"], Units(await ListAsync(h, Manual(2, 19))));
        Assert.Equal(["TP140-101", "TP140-102"], Units(await ListAsync(h, Manual(1) with { TowerId = 7 })));
        Assert.Equal(["TP141-101"], Units(await ListAsync(h, Manual(1) with { TowerId = 8 })));
        Assert.Equal(["TP140-101", "TP140-102", "TP141-101"], Units(await ListAsync(h, Manual(1) with { TowerIds = [7, 8] })));
        Assert.Empty((await ListAsync(h, Manual(19) with { TowerId = 8 })).Items);                     // never widened to the other tower
        Assert.DoesNotContain("TP140-P12", Units(await ListAsync(h, Manual(0, 1, 2, 19))));            // a chosen floor never matches a non-apartment
    }

    [Fact]
    public async Task Manual_FloorTogetherWithMinimumTotal_PaymentStatusAndDates()
    {
        using var h = await SeededAsync();
        Assert.Equal(["TP140-101"], Units(await ListAsync(h, Manual(1) with { MinRemaining = 450m })));                 // 101 (500) stays; 102 (400) and 141's 101 (250) fall below
        Assert.Equal(["TP140-101", "TP140-102", "TP141-101"], Units(await ListAsync(h, Manual(1) with { DueStatus = "Due" })));
        Assert.Empty((await ListAsync(h, Manual(1) with { DueStatus = "Overdue" })).Items);                            // nothing on floor 1 is overdue
        Assert.Equal(["TP140-101", "TP140-102", "TP141-101"], Units(await ListAsync(h, Manual(1) with { DueFrom = new(2026, 10, 1), DueTo = new(2026, 10, 31) })));
        Assert.Empty((await ListAsync(h, Manual(1) with { DueFrom = new(2026, 11, 1), DueTo = new(2026, 11, 30) })).Items);
    }

    [Fact]
    public async Task AnInvalidFloor_IsRefused_NotTurnedIntoAllFloors()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var refused = await scope.Query.QueryAsync(h.Manager, Manual() with { Floors = [1, 100] }, 1, 25, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("floors", refused.Detail);
    }

    // ------------------------------------------------------------ preview == execution

    [Theory]
    [InlineData("Manual")]
    [InlineData("Auto")]
    public async Task ThePreviewedRecipients_AreTheApprovedAndUploadedRecipients(string mode)
    {
        using var h = await SeededAsync(advanceDay: mode == "Manual");              // Auto: 14 Oct, the Current Month day
        ReviewFilter Filter(params int[] floors) => mode == "Manual" ? Manual(floors) : Auto(floors);
        var listed = await ListAsync(h, Filter(1, 2));
        var readyListed = listed.Items.Where(i => i.ValidationStatus == "Ready").Select(i => i.CustomerName).Order().ToArray();
        Assert.NotEmpty(readyListed);

        var (summary, confirm) = await h.PrepareAsync(new SelectionRequest(Filter(1, 2), ReviewQueryService.ModeAllMatching), $"floors-{mode}");
        Assert.Equal(readyListed.Length, summary.Count);                              // the approval counts what the list showed as Ready
        Assert.Equal(readyListed, summary.Contacts.Select(c => c.CustomerName).Order().ToArray());

        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var uploaded = Assert.Single(h.Client.Calls).Contacts.Select(c => c.CustomerName).Order().ToArray();
        Assert.Equal(readyListed, uploaded);                                          // and the upload carries exactly them: nobody from another floor

        // The floors travel with the stored approval, so an audit shows which filter produced the list.
        var dispatch = await scope.Store.GetDispatchAsync(h.Scheduler.Dispatches.Single() is var id ? (await scope.Store.GetDispatchByIdAsync(id, CancellationToken.None))!.PublicId : Guid.Empty, CancellationToken.None);
        Assert.Equal([1, 2], JsonSerializer.Deserialize<ReviewFilter>(dispatch!.FilterJson!)!.Floors!.ToArray());
    }

    [Fact]
    public async Task SelectedCustomers_AreStillNarrowedByTheFloors()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var keys = new[] { "4|T0101|TP140-101", "4|T0201|TP140-201" };
        var request = new SelectionRequest(Manual(1), ReviewQueryService.ModeSelectedUnits, UnitKeys: keys);
        var summary = (await scope.Query.SummarizeAsync(h.Manager, request, CancellationToken.None)).Value!;
        Assert.Equal(["Customer 101"], summary.Contacts.Select(c => c.CustomerName).ToArray());                // 201 is on floor 2: ticked but not on the chosen floor, so not sent
    }

    // ------------------------------------------------------------ Auto stays as it was

    [Fact]
    public async Task TheScheduledAutoJob_HasNoFloorFilter_AndDispatchesTheWholeCampaignAsBefore()
    {
        using var h = new ReviewHarness();
        h.Campaign.AutoSendingEnabled = true;
        h.Source.Items.AddRange(Rows());
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var result = await scope.Auto.RunAsync(CancellationToken.None);
        var stage = Assert.Single(result.Stages, s => s.Stage == "CurrentMonth");
        Assert.Equal("Dispatched", stage.Outcome);
        var ready = await ListAsync(h, Auto() with { MinRemaining = null });                                      // the job's own filter: the review default minimum, no floors
        Assert.Equal(ready.Items.Count(i => i.ValidationStatus is "Ready" or "AlreadySent"), stage.Count);       // every Ready customer, whatever the floor (the parking unit owes less than the minimum)
        Assert.Equal(5, stage.Count);
        Assert.Null(JsonSerializer.Deserialize<ReviewFilter>(JsonSerializer.Serialize(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Auto")))!.Floors);
    }

    [Fact]
    public void StoredFilters_WrittenBeforeFloorsExisted_StillReadAsAllFloors()
    {
        const string old = "{\"CompanyId\":null,\"Project\":null,\"Unit\":null,\"Customer\":null,\"Year\":null,\"Month\":null,\"DueFrom\":null,\"DueTo\":null,\"PaymentStatus\":null,\"MinRemaining\":0,\"MaxRemaining\":null,\"ReminderType\":\"CurrentMonth\",\"ValidationStatus\":null,\"Reason\":null,\"DueStatus\":null,\"TowerId\":null,\"TowerIds\":null,\"SendingMode\":\"Manual\",\"ContactList\":null}";
        var filter = JsonSerializer.Deserialize<ReviewFilter>(old)!;
        Assert.Null(filter.Floors);
        Assert.NotNull(ManualFilters.FromStored(old));                                      // its amount criteria are read exactly as before
    }
}
