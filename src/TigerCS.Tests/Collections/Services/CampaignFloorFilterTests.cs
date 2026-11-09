using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Services;

/// <summary>
/// The Floor filter of the Auto preview: apartment floors (from the unit number) together with Project / Tower, Payment status, dates and Minimum Total. It only narrows the finished list, so counts, totals,
/// pages and both exports agree, a unit keeps exactly the figures and status it has without the filter, and no Floor filter changes nothing.
/// </summary>
public sealed class CampaignFloorFilterTests
{
    private const string Stage = "CurrentMonthReminder";

    /// <summary>
    /// Clock = 2026-10-14. Tower 7 (140): 101 and 102 (floor 1; October 500 / 400), 201 (floor 2; October 700), 1921 (floor 19; October 300 Due + September 200 Overdue), P12 (a parking unit, no floor; October 90).
    /// Tower 8 (141): 101 (floor 1; October 250).
    /// </summary>
    private static CollectionsCampaignAppServiceTests.Harness Seeded()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        PactReceivableInstalment Row(string code, int id, string tenant, decimal amount, int tower, DateTime due) =>
            CollectionsCampaignAppServiceTests.Row(due.Day, amount, tenant, unit: id) with
            { UnitCode = code, TowerId = tower, TowerNumber = tower == 7 ? "140" : "141", TowerName = tower == 7 ? "Al Ghaf" : "Al Sidr", DueDate = due };
        h.Source.Items.Add(Row("TP140-101", 101, "3001", 500m, 7, new(2026, 10, 10)));
        h.Source.Items.Add(Row("TP140-102", 102, "3002", 400m, 7, new(2026, 10, 12)));
        h.Source.Items.Add(Row("TP140-201", 201, "3003", 700m, 7, new(2026, 10, 9)));
        h.Source.Items.Add(Row("TP140-1921", 1921, "3004", 300m, 7, new(2026, 10, 8)));
        h.Source.Items.Add(Row("TP140-1921", 1921, "3004", 200m, 7, new(2026, 9, 8)));
        h.Source.Items.Add(Row("TP140-P12", 12, "3005", 90m, 7, new(2026, 10, 11)));
        h.Source.Items.Add(Row("TP141-101", 5101, "4001", 250m, 8, new(2026, 10, 9)));
        return h;
    }

    private static string[] Units(CollectionsCampaignPreviewDto r) => r.Items.Select(i => i.UnitCode).Order().ToArray();

    [Fact]
    public async Task NoFloorFilter_IsAllFloors_AndChangesNothing()
    {
        var h = Seeded();
        var baseline = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m)).Value!;
        var empty = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, floors: [])).Value!;
        var none = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, floors: null)).Value!;
        Assert.Equal(["TP140-101", "TP140-102", "TP140-1921", "TP140-201", "TP140-P12", "TP141-101"], Units(baseline));   // the parking unit is listed: All Floors does not filter
        Assert.Equal(Units(baseline), Units(empty)); Assert.Equal(Units(baseline), Units(none));
        Assert.Null(baseline.Floors); Assert.Null(empty.Floors);
        Assert.Equal(baseline.TotalCount, empty.TotalCount);
    }

    [Fact]
    public async Task ASingleFloor_ListsOnlyThatFloorsApartments_AcrossAllTowers()
    {
        var h = Seeded();
        var floor1 = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, floors: [1])).Value!;
        Assert.Equal(["TP140-101", "TP140-102", "TP141-101"], Units(floor1));
        Assert.Equal(3, floor1.TotalCount);
        Assert.Equal([1], floor1.Floors!.ToArray());
        var floor19 = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, floors: [19])).Value!;
        Assert.Equal(["TP140-1921"], Units(floor19));
    }

    [Fact]
    public async Task SeveralFloors_AreOr_AndEveryUnitKeepsItsFiguresAndStatus()
    {
        var h = Seeded();
        var all = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m)).Value!;
        var some = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, floors: [2, 19])).Value!;
        Assert.Equal(["TP140-1921", "TP140-201"], Units(some));
        // Exactly the unit the unfiltered list shows: the filter narrows, it never recomputes.
        foreach (var item in some.Items) Assert.Equal(all.Items.Single(i => i.RecordId == item.RecordId), item);
        // The qualifying totals cover the filtered list only.
        Assert.Equal(some.Items.Where(i => i.Status != "NotEligible").Sum(i => i.Amount ?? 0m), some.EligibleTotalAmount);
        Assert.Equal(some.Items.Sum(i => i.TotalAmount), some.Items.Sum(i => i.DueAmount + i.OverdueAmount));
    }

    [Fact]
    public async Task EveryListedFloor_StillExcludesUnitsThatAreNotApartments()
    {
        var h = Seeded();
        var every = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, floors: [0, 1, 2, 19])).Value!;
        Assert.DoesNotContain("TP140-P12", Units(every));                        // choosing floors is a statement about apartments
        Assert.Equal(5, every.TotalCount);
    }

    [Fact]
    public async Task FloorsAndTowers_Multiple_Combine()
    {
        var h = Seeded();
        var both = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, towerIds: [7, 8], floors: [1])).Value!;
        Assert.Equal(["TP140-101", "TP140-102", "TP141-101"], Units(both));
        var onlyEight = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, towerId: 8, floors: [1, 2])).Value!;
        Assert.Equal(["TP141-101"], Units(onlyEight));
        var none = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, towerId: 8, floors: [19])).Value!;     // a floor the tower does not have: nothing, never widened
        Assert.Empty(none.Items); Assert.Equal(0, none.TotalCount);
    }

    [Theory]
    [InlineData("Both", 1, new[] { "TP140-101", "TP140-102", "TP141-101" })]
    [InlineData("Due", 19, new[] { "TP140-1921" })]
    [InlineData("Overdue", 19, new[] { "TP140-1921" })]       // the September instalment of 1921
    [InlineData("Overdue", 1, new string[0])]                  // nothing overdue on floor 1
    public async Task PaymentStatus_AndFloor_Together(string status, int floor, string[] units)
    {
        var h = Seeded();
        var report = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, paymentStatus: status, floors: [floor])).Value!;
        Assert.Equal(units.Order(), Units(report));
        Assert.Equal(units.Length, report.TotalCount);
    }

    [Fact]
    public async Task DatesMinimumTotalStatusTowerAndFloor_AllTogether()
    {
        var h = Seeded();
        // Floor 1, tower 7, Due, instalments due 11-31 October, more than AED 350: only 102 (its October 400 is the one due on the 12th).
        var report = (await h.Service.PreviewAsync(h.Manager, Stage, towerId: 7, floors: [1], dateFrom: new(2026, 10, 11), dateTo: new(2026, 10, 31), minTotal: 350m, paymentStatus: "Due")).Value!;
        Assert.Equal(["TP140-102"], Units(report));
        // The minimum applies to the unit's total, strictly greater: 400 is not greater than 400.
        Assert.Empty((await h.Service.PreviewAsync(h.Manager, Stage, towerId: 7, floors: [1], dateFrom: new(2026, 10, 11), dateTo: new(2026, 10, 31), minTotal: 400m, paymentStatus: "Due")).Value!.Items);
        // Minimum Total only: floor 1 with more than AED 450 keeps 101 (500) and drops 102 (400) and 141's 101 (250).
        Assert.Equal(["TP140-101"], Units((await h.Service.PreviewAsync(h.Manager, Stage, floors: [1], minTotal: 450m)).Value!));
    }

    [Fact]
    public async Task Pages_Counts_AndBothExports_AgreeWithTheFilteredList()
    {
        var h = Seeded();
        var page1 = (await h.Service.PreviewAsync(h.Manager, Stage, page: 1, pageSize: 1, minTotal: 0m, floors: [1, 2])).Value!;
        var page2 = (await h.Service.PreviewAsync(h.Manager, Stage, page: 2, pageSize: 1, minTotal: 0m, floors: [1, 2])).Value!;
        Assert.Equal(4, page1.TotalCount); Assert.Equal(4, page2.TotalCount);                           // 101, 102, 201, 141's 101
        Assert.Equal(page1.EligibleTotalAmount, page2.EligibleTotalAmount);                              // the whole filtered list, not the page
        var whole = (await h.Service.PreviewAsync(h.Manager, Stage, pageSize: 100, minTotal: 0m, floors: [1, 2])).Value!;
        Assert.Equal(2, page1.Items.Concat(page2.Items).Select(i => i.RecordId).Distinct().Count());           // no page repeats or skips a unit
        Assert.All(page1.Items.Concat(page2.Items), i => Assert.Contains(whole.Items, w => w.RecordId == i.RecordId));
        var review = (await h.Service.ExportAsync(h.Manager, Stage, "review", minTotal: 0m, floors: [1, 2])).Value!;
        Assert.Equal(whole.TotalCount, review.RowCount);
        foreach (var unit in new[] { "TP140-101", "TP140-102", "TP140-201", "TP141-101", "TP140-1921", "TP140-P12" })
            Assert.Equal(whole.Items.Any(i => i.UnitCode == unit), review.Csv.Contains($"\"{unit}\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInvalidFloor_IsRefused_NeverTurnedIntoAllFloors()
    {
        var h = Seeded();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, Stage, floors: [-1])).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, Stage, floors: [1, 100])).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, Stage, "review", floors: [500])).Outcome);
    }

    [Fact]
    public async Task TheFloorFilter_DoesNotReadAnythingExtra_OneReadPerTowerAsBefore()
    {
        var h = Seeded();
        await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, floors: [1]);
        Assert.Equal(1, h.Source.Reads);                         // the same single window read; the floor is applied to the finished units
    }
}
