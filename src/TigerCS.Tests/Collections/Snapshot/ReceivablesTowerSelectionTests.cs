using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>
/// Receivables Tower multi-select: none / one / several / every tower, invalid selections. The tower set is applied to the instalments BEFORE they are grouped into units, totalled
/// and paged (one read, never one per tower), so units, Due / Overdue totals, pages and the instalment view all describe exactly the chosen towers and nothing appears twice.
/// The fake source is the reference model of dbo.usp_CollectionsRcv_GetUnits; RcvRealSqlTests.TowerSelection covers the real procedure.
/// </summary>
public sealed class ReceivablesTowerSelectionTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CollectionsCaller Agent = new(Guid.NewGuid(), [Roles.CsAgent], []);

    // Fake catalogue: 1 = tower 124 (company 4), 2 = tower 136 (company 4), 3 = tower 127 (company 32).
    private static (PactInstalmentsAppService Service, FakeInstalmentSource Source) Build()
    {
        var source = new FakeInstalmentSource(Now);
        var options = new CollectionsOptions { Enabled = true };
        var service = new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(Now)),
            source, NullLogger<PactInstalmentsAppService>.Instance);
        // Tower 124: two units (one Overdue + one Due instalment each); tower 136: one unit, Overdue; tower 127 (Sharjah): one unit, Due.
        source.Rows.AddRange([
            Row(4, "A", 11, "TP124-11", 8, 300m), Row(4, "A", 11, "TP124-11", 10, 200m),
            Row(4, "B", 12, "TP124-12", 7, 150m), Row(4, "B", 12, "TP124-12", 10, 250m),
            Row(4, "C", 21, "TP136-21", 9, 500m),
            Row(32, "D", 31, "TP127-31", 10, 400m)]);
        return (service, source);
    }

    private static FakeInstalment Row(int company, string tenant, int unit, string code, int month, decimal remaining) =>
        new(company, tenant, "Customer " + tenant, unit, code, "V" + unit + month, new DateTime(2026, month, 5), remaining + 100, 100, remaining, "Installment", true);

    private static Task<CollectionsResult<PactInstalmentsPageDto>> Units(PactInstalmentsAppService service, int? towerId = null, IReadOnlyList<int>? towerIds = null, string? status = null,
        string? search = null, int page = 1, int pageSize = 25, string? view = "units") =>
        service.ListAsync(Agent, towerId, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "outstanding", 0m, search, page, pageSize, default, view, null, status, null, towerIds);

    private static string[] Tenants(PactInstalmentsPageDto page) => page.Units!.Select(u => u.TenantId).Order().ToArray();

    [Fact]
    public async Task NoTowerSelected_MeansAllTowers_AndSendsNoTowerFilter()
    {
        var (service, source) = Build();
        var page = (await Units(service)).Value!;
        Assert.Equal(["A", "B", "C", "D"], Tenants(page));
        Assert.Null(source.LastRequest!.TowerId); Assert.Null(source.LastRequest.TowerIds);
        Assert.Null(page.TowerId); Assert.Null(page.TowerIds);
        Assert.Equal(4, page.Totals.UnitCount);
        var empty = (await Units(service, towerIds: [])).Value!;                          // an empty selection is also "all"
        Assert.Equal(4, empty.Totals.UnitCount); Assert.Null(source.LastRequest.TowerIds);
    }

    [Fact]
    public async Task OneTower_UsesTheEstablishedSingleTowerPath_AndFiltersToIt()
    {
        var (service, source) = Build();
        var viaOld = (await Units(service, towerId: 1)).Value!;                           // old URL / old caller
        Assert.Equal(1, source.LastRequest!.TowerId); Assert.Null(source.LastRequest.TowerIds);
        Assert.Equal(["A", "B"], Tenants(viaOld)); Assert.Equal(1, viaOld.TowerId);
        var viaList = (await Units(service, towerIds: [1])).Value!;                       // a one-element list is the same request
        Assert.Equal(1, source.LastRequest.TowerId); Assert.Null(source.LastRequest.TowerIds);
        Assert.Equal(Tenants(viaOld), Tenants(viaList));
        Assert.Equal(viaOld.Totals, viaList.Totals);
    }

    [Fact]
    public async Task SeveralTowers_AreCombinedWithOr_InOneRead_AndTotalsCoverExactlyThem()
    {
        var (service, source) = Build();
        var readsBefore = source.Reads;
        var page = (await Units(service, towerIds: [1, 3])).Value!;                       // 124 (Dubai) + 127 (Sharjah): towers of different companies
        Assert.Equal(readsBefore + 1, source.Reads);                                      // ONE query for the whole selection, not one per tower
        Assert.Equal([1, 3], source.LastRequest!.TowerIds); Assert.Null(source.LastRequest.TowerId);
        Assert.Equal(["A", "B", "D"], Tenants(page)); Assert.Equal([1, 3], page.TowerIds);
        // Overdue 300 + 150, Due 200 + 250 + 400: Due + Overdue is untouched, only restricted to the chosen towers.
        Assert.Equal((3, 5), (page.Totals.UnitCount, page.Totals.Count));
        Assert.Equal((450m, 850m, 1300m), (page.Totals.OverdueRemaining, page.Totals.DueRemaining, page.Totals.RemainingTotal));
        Assert.Equal(page.Totals.RemainingTotal, page.Units!.Sum(u => u.RemainingTotal));
    }

    [Fact]
    public async Task EveryTowerAndSeveralIsASubsetOfTheAllTowersResult_NeverMoreAndNeverDuplicated()
    {
        var (service, _) = Build();
        var all = (await Units(service)).Value!;
        var some = (await Units(service, towerIds: [2, 3])).Value!;
        Assert.Equal(["C", "D"], Tenants(some));
        Assert.All(some.Units!, u => Assert.Contains(all.Units!, a => a.TenantId == u.TenantId && a.UnitCode == u.UnitCode));
        Assert.Equal(some.Units!.Count, some.Units.Select(u => (u.CompanyId, u.TenantId, u.UnitId, u.UnitCode)).Distinct().Count());   // a unit appears once
        var every = (await Units(service, towerIds: [1, 2, 3])).Value!;                   // the whole catalogue = all towers
        Assert.Equal(all.Totals, every.Totals);
        Assert.Equal(all.Units!.Count, every.Units!.Count);
    }

    [Fact]
    public async Task DuplicatesAndTheLegacyTowerIdCollapse_SoNothingIsCountedTwice()
    {
        var (service, source) = Build();
        var page = (await Units(service, towerId: 1, towerIds: [1, 2, 2, 1])).Value!;     // old towerId plus a list that repeats it
        Assert.Equal([1, 2], source.LastRequest!.TowerIds);
        Assert.Equal(["A", "B", "C"], Tenants(page));
        Assert.Equal(3, page.Totals.UnitCount);
        var same = (await Units(service, towerId: 2, towerIds: [2, 2])).Value!;          // the same tower however often it is named = one tower
        Assert.Equal(2, source.LastRequest.TowerId); Assert.Null(source.LastRequest.TowerIds);
        Assert.Equal(["C"], Tenants(same));
    }

    [Fact]
    public async Task PagingAndTotals_AreComputedOverTheFilteredSet_BeforeThePageIsCut()
    {
        var (service, source) = Build();
        source.Rows.Clear();
        for (var u = 1; u <= 9; u++)
        {
            (int company, string code) = (u % 3) switch { 0 => (4, $"TP124-{u}"), 1 => (4, $"TP136-{u}"), _ => (32, $"TP127-{u}") };
            source.Rows.Add(Row(company, "T" + u, u, code, 8, 100m + u));
            source.Rows.Add(Row(company, "T" + u, u, code, 10, 50m));
        }
        var seen = new List<string>();
        PactInstalmentsPageDto? first = null;
        for (var p = 1; p <= 4; p++)
        {
            var page = (await Units(service, towerIds: [1, 2], pageSize: 2, page: p)).Value!;   // towers 124 + 136: 6 units
            first ??= page;
            seen.AddRange(page.Units!.Select(u => u.TenantId));
            Assert.Equal(first.Totals, page.Totals);                                            // totals describe the whole selection on every page
        }
        Assert.Equal(6, first!.Totals.UnitCount); Assert.Equal(6, seen.Count); Assert.Equal(6, seen.Distinct().Count());   // pages are complete and disjoint
        Assert.All(seen, t => Assert.NotEqual(2, int.Parse(t[1..]) % 3));                       // no unit of tower 127 (u % 3 == 2) leaked in
        Assert.Equal(12, first.Totals.Count);
    }

    [Fact]
    public async Task StatusSearchAndMinimum_ApplyTogetherWithTheTowerSet()
    {
        var (service, _) = Build();
        var overdue = (await Units(service, towerIds: [1, 2], status: "overdue")).Value!;            // units with an Overdue instalment in 124 + 136
        Assert.Equal(["A", "B", "C"], Tenants(overdue));
        var due = (await Units(service, towerIds: [2, 3], status: "due")).Value!;                    // 136 has no Due instalment; 127 has one
        Assert.Equal(["D"], Tenants(due));
        var searched = (await Units(service, towerIds: [1, 2, 3], search: "Customer B")).Value!;
        Assert.Equal(["B"], Tenants(searched));
        var outside = (await Units(service, towerIds: [2], search: "Customer B")).Value!;           // the search never reaches beyond the chosen towers
        Assert.Empty(outside.Units!); Assert.Equal(0, outside.Totals.UnitCount);
    }

    [Fact]
    public async Task TheInstalmentView_FiltersByTheSameTowerSet()
    {
        var (service, _) = Build();
        var page = (await Units(service, towerIds: [1, 3], view: "instalments")).Value!;
        Assert.Equal(5, page.Totals.Count);
        Assert.Equal(new[] { "A", "A", "B", "B", "D" }, page.Items.Select(i => i.TenantId).Order());
        Assert.Equal(1300m, page.Totals.RemainingTotal);
        Assert.Equal([1, 3], page.TowerIds);
    }

    [Fact]
    public async Task ADeselectedTowerReturnsNothingFromThatTower_EvenWhenAnotherCompanyIsChosen()
    {
        var (service, _) = Build();
        var page = (await Units(service, towerIds: [2, 3])).Value!;
        Assert.DoesNotContain(page.Units!, u => u.UnitCode.StartsWith("TP124", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(999)]            // unknown tower
    public async Task AnUnknownTower_InsideASelection_InvalidatesTheWholeRequest_AndShowsNothing(int unknown)
    {
        var (service, _) = Build();
        var result = await Units(service, towerIds: [1, unknown]);
        Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Units(service, towerId: unknown)).Outcome);   // as before for one tower
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveTowerIds_AreRejected_WithoutReadingAnything(int bad)
    {
        var (service, source) = Build();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Units(service, towerIds: [1, bad])).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Units(service, towerId: bad)).Outcome);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task AbsurdlyLongSelections_AreRejected()
    {
        var (service, source) = Build();
        var result = await Units(service, towerIds: Enumerable.Range(1, PactInstalmentsAppService.MaxTowers + 1).ToList());
        Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(0, source.Reads);
    }
}
