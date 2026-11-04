using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Tests.Collections.Rcv;

public sealed partial class RcvRealSqlTests
{
    private static async Task<int> TowerId(int company, int number) =>
        await Scalar<int>($"SELECT TowerId FROM dbo.CollectionsTowers WHERE CompanyId = {company} AND TowerNumber = {number}");

    /// <summary>The By unit read with a tower selection: one tower as TowerId (the established path), several as TowerIds. No Minimum Total, so every unit with a Due / Overdue amount counts.</summary>
    private static Task<PactInstalmentUnitsPage> ByTowers(int[]? towers = null, string? status = null, string? search = null, int page = 1, int pageSize = 100)
    {
        towers ??= [];
        return Source().ReadInstalmentUnitsAsync(new PactInstalmentsRequest(new DateOnly(2000, 1, 1), Today, Today, 0m, "outstanding", search, null, page, pageSize, null,
            towers.Length == 1 ? towers[0] : null, status, true, null, towers.Length > 1 ? towers : null), default);
    }

    [SqlFact]
    public async Task TowerSelection_SeveralTowersAreOr_SumToTheirParts_AndNeverDuplicateAUnit()
    {
        await Seed();
        int t101 = await TowerId(4, 101), t102 = await TowerId(4, 102), t105 = await TowerId(4, 105), t127s = await TowerId(32, 127);
        var all = await ByTowers();
        var parts = new[] { await ByTowers([t101]), await ByTowers([t102]), await ByTowers([t127s]) };
        var combined = await ByTowers([t101, t102, t127s]);
        // OR: exactly the union of the single-tower reads, each unit once, totals equal to the sum of the parts (Due and Overdue stay separate).
        Assert.Equal(parts.SelectMany(p => p.Units.Select(Key)).Order(), combined.Units.Select(Key).Order());
        Assert.Equal(combined.Units.Count, combined.Units.Select(Key).Distinct().Count());
        Assert.Equal(parts.Sum(p => p.Totals.UnitCount), combined.Totals.UnitCount);
        Assert.Equal(parts.Sum(p => p.Totals.RemainingTotal), combined.Totals.RemainingTotal);
        Assert.Equal(parts.Sum(p => p.Totals.DueRemaining), combined.Totals.DueRemaining);
        Assert.Equal(parts.Sum(p => p.Totals.OverdueRemaining), combined.Totals.OverdueRemaining);
        Assert.Equal(combined.Totals.RemainingTotal, combined.Units.Sum(u => u.RemainingTotal));
        Assert.True(combined.Totals.UnitCount < all.Totals.UnitCount);                      // really a subset
        // The same TowerNumber in the OTHER company (Sharjah's TP101-101) is not part of Dubai's tower 101.
        Assert.DoesNotContain(combined.Units, u => u is { CompanyId: 32, TowerNumber: "101" });
        Assert.Contains(combined.Units, u => u is { CompanyId: 32, TowerNumber: "127" });   // ... while Sharjah's tower 127 chosen explicitly is
        Assert.DoesNotContain(combined.Units, u => u.TowerNumber == "105");                 // an unselected tower stays out
        Assert.Equal(all.Units.Count(u => u.TowerNumber == "105"), (await ByTowers([t105])).Units.Count);
    }

    [SqlFact]
    public async Task TowerSelection_NoTowerIsAllTowers_AndEveryTowerGivesTheSameResult()
    {
        await Seed();
        var all = await ByTowers();
        var catalogue = (await Rows("SELECT TowerId FROM dbo.CollectionsTowers")).Select(r => int.Parse(r[0]!)).ToArray();
        var every = await ByTowers(catalogue);
        Assert.Equal(all.Units.Select(Key), every.Units.Select(Key));
        Assert.Equal(all.Totals, every.Totals);
    }

    [SqlFact]
    public async Task TowerSelection_IsAppliedBeforeAggregationAndPaging_SoPagesAreCompleteAndDisjoint()
    {
        await Seed();
        int[] chosen = [await TowerId(4, 101), await TowerId(4, 102), await TowerId(4, 103), await TowerId(4, 105)];
        var whole = await ByTowers(chosen);
        Assert.True(whole.Totals.UnitCount >= 3);
        var seen = new List<string>();
        for (var p = 1; p <= whole.Totals.UnitCount; p++)
        {
            var page = await ByTowers(chosen, page: p, pageSize: 1);
            Assert.Equal(whole.Totals, page.Totals);                                         // the totals never depend on the page
            seen.AddRange(page.Units.Select(Key));
        }
        Assert.Equal(whole.Units.Select(Key), seen);                                         // same order, nothing skipped, nothing repeated
    }

    [SqlFact]
    public async Task TowerSelection_CombinesWithStatusAndSearch_AndNeverSearchesOutsideTheChosenTowers()
    {
        await Seed();
        int[] chosen = [await TowerId(4, 101), await TowerId(4, 102)];
        var overdue = await ByTowers(chosen, status: "overdue");
        var due = await ByTowers(chosen, status: "due");
        Assert.All(overdue.Units, u => Assert.Contains(u.Instalments, i => i.Classification == "Overdue"));
        Assert.All(due.Units, u => Assert.Contains(u.Instalments, i => i.Classification == "Due"));
        Assert.Equal(0, (await ByTowers(chosen, search: "Dalia")).Totals.UnitCount);          // Dalia Five lives in tower 105: not chosen
        Assert.Equal(1, (await ByTowers([.. chosen, await TowerId(4, 105)], search: "Dalia")).Totals.UnitCount);
    }

    [SqlFact]
    public async Task TowerSelection_AnUnknownTowerAnywhereInTheSelectionIsRejected()
    {
        await Seed();
        var good = await TowerId(4, 101);
        await Assert.ThrowsAsync<PactReceivablesScopeException>(() => ByTowers([good, 987654]));
        await Assert.ThrowsAsync<PactReceivablesScopeException>(() => ByTowers([987654, 987655]));
        await Assert.ThrowsAsync<PactReceivablesScopeException>(() => ByTowers([987654]));       // as before for one tower
    }
}
