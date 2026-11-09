using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>
/// Receivables Floor multi-select: none = All Floors (nothing sent), one / several floors, floors with towers, status and Minimum Total, invalid selections, and the Floor dropdown's own list. The floor
/// set is applied to the instalments BEFORE they are grouped into units, totalled and paged. The fake source is the reference model of dbo.usp_CollectionsRcv_GetUnits (with @Floors);
/// RcvRealSqlTests would cover the real procedure on a development SQL Server.
/// </summary>
public sealed class ReceivablesFloorSelectionTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CollectionsCaller Agent = new(Guid.NewGuid(), [Roles.CsAgent], []);

    private static (PactInstalmentsAppService Service, FakeInstalmentSource Source) Build()
    {
        var source = new FakeInstalmentSource(Now);
        var options = new CollectionsOptions { Enabled = true };
        var service = new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(Now)),
            source, NullLogger<PactInstalmentsAppService>.Instance);
        // Tower 124: 101 (floor 1: Overdue 300 + Due 200), 102 (floor 1: Due 250), 201 (floor 2: Overdue 500), 1921 (floor 19: Due 150), P12 (parking). Tower 136: 101 (floor 1: Due 400).
        source.Rows.AddRange([
            Row(4, "A", 101, "TP124-101", 8, 300m), Row(4, "A", 101, "TP124-101", 10, 200m),
            Row(4, "B", 102, "TP124-102", 10, 250m),
            Row(4, "C", 201, "TP124-201", 9, 500m),
            Row(4, "D", 1921, "TP124-1921", 10, 150m),
            Row(4, "E", 12, "TP124-P12", 10, 90m),
            Row(4, "F", 5101, "TP136-101", 10, 400m)]);
        return (service, source);
    }

    private static FakeInstalment Row(int company, string tenant, int unit, string code, int month, decimal remaining) =>
        new(company, tenant, "Customer " + tenant, unit, code, "V" + unit + month, new DateTime(2026, month, 5), remaining + 100, 100, remaining, "Installment", true);

    private static Task<CollectionsResult<PactInstalmentsPageDto>> Units(PactInstalmentsAppService service, IReadOnlyList<int>? floors = null, IReadOnlyList<int>? towerIds = null,
        string? status = null, decimal? minTotal = null, int page = 1, int pageSize = 25) =>
        service.ListAsync(Agent, null, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "outstanding", 0m, null, page, pageSize, default, "units", null, status, minTotal, towerIds, floors);

    private static string[] Tenants(PactInstalmentsPageDto page) => page.Units!.Select(u => u.TenantId).Order().ToArray();

    [Fact]
    public async Task NoFloorSelected_MeansAllFloors_AndSendsNoFloorFilter()
    {
        var (service, source) = Build();
        var all = (await Units(service)).Value!;
        Assert.Equal(["A", "B", "C", "D", "E", "F"], Tenants(all));                       // the parking unit is listed: All Floors does not filter
        Assert.Null(source.LastRequest!.Floors); Assert.Null(all.Floors);
        var empty = (await Units(service, floors: [])).Value!;
        Assert.Equal(Tenants(all), Tenants(empty)); Assert.Null(source.LastRequest.Floors);
    }

    [Fact]
    public async Task OneAndSeveralFloors_FilterUnitsTotalsAndCounts_BeforeGrouping()
    {
        var (service, source) = Build();
        var one = (await Units(service, floors: [1])).Value!;
        Assert.Equal(["A", "B", "F"], Tenants(one));
        Assert.Equal(3, one.Totals.UnitCount); Assert.Equal(300m + 200m + 250m + 400m, one.Totals.RemainingTotal);
        Assert.Equal([1], source.LastRequest!.Floors!.ToArray()); Assert.Equal([1], one.Floors!.ToArray());
        var several = (await Units(service, floors: [19, 2, 2])).Value!;                   // order and duplicates do not matter
        Assert.Equal(["C", "D"], Tenants(several));
        Assert.Equal([2, 19], source.LastRequest.Floors!.ToArray());
        Assert.Equal(500m + 150m, several.Totals.RemainingTotal);
    }

    [Fact]
    public async Task FloorsCombineWithTowersStatusAndMinimumTotal()
    {
        var (service, _) = Build();
        Assert.Equal(["A", "B"], Tenants((await Units(service, floors: [1], towerIds: [1])).Value!));
        Assert.Equal(["F"], Tenants((await Units(service, floors: [1], towerIds: [2])).Value!));
        Assert.Equal(["A", "B", "F"], Tenants((await Units(service, floors: [1, 2], towerIds: [1, 2], status: "due")).Value!));
        Assert.Equal(["A", "C"], Tenants((await Units(service, floors: [1, 2], status: "overdue", minTotal: 400m)).Value!));   // units with an overdue instalment whose total is above 400: A (500) and C (500); B has none
        Assert.Equal(["A", "F"], Tenants((await Units(service, floors: [1], minTotal: 300m)).Value!));                        // strictly greater than 300: A (500) and F (400); B (250) is out
        Assert.Empty((await Units(service, floors: [19], towerIds: [2])).Value!.Units!);                                     // no such floor in that tower: nothing, never widened
    }

    [Fact]
    public async Task InvalidFloors_AreRefused_NeverTurnedIntoAllFloors()
    {
        var (service, source) = Build();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Units(service, floors: [-1])).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Units(service, floors: [1, 100])).Outcome);
        Assert.Equal(0, source.Reads);
    }

    // ------------------------------------------------------------ the Floor dropdown

    private sealed class Directory(params string[] codes) : IUnitFloorDirectory
    {
        public IReadOnlyList<int>? LastTowers { get; private set; }
        public Task<IReadOnlyList<string>> ListUnitCodesAsync(IReadOnlyList<int> towerIds, CancellationToken cancellationToken)
        {
            LastTowers = towerIds; return Task.FromResult<IReadOnlyList<string>>(codes);
        }
    }

    private static PactReceivableCustomersAppService Floors(IUnitFloorDirectory? directory)
    {
        var options = new CollectionsOptions { Enabled = true };
        return new PactReceivableCustomersAppService(options, new PactReceivablesOptions { Enabled = true }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(Now)),
            new FakeInstalmentSource(Now), NullLogger<PactReceivableCustomersAppService>.Instance, floorDirectory: directory);
    }

    [Fact]
    public async Task TheFloorList_HasOnlyApartmentFloors_WithTheirUnitCounts_Ordered()
    {
        var directory = new Directory("TP124-1921", "TP124-101", "TP124-102", "TP124-201", "TP124-P12", "TP124-SHOP1", "TP124-12", "tp124-101", "TP124-001", "TP124-513*", "TP136-601-A");
        var list = (await Floors(directory).ListFloorsAsync(Agent, [2, 1])).Value!;
        Assert.Equal([0, 1, 2, 6, 19], list.Floors.Select(f => f.Floor).ToArray());
        Assert.Equal(["Ground floor", "Floor 1", "Floor 2", "Floor 6", "Floor 19"], list.Floors.Select(f => f.Label).ToArray());
        Assert.Equal(2, list.Floors.Single(f => f.Floor == 1).UnitCount);                      // 101 (once, whatever the case) and 102
        Assert.Equal([1, 2], directory.LastTowers!.ToArray()); Assert.Equal([1, 2], list.TowerIds.ToArray());
    }

    [Fact]
    public async Task TheFloorList_RefusesBadTowers_AndNeedsTheReadPermissionAndTheDirectory()
    {
        var directory = new Directory("TP124-101");
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Floors(directory).ListFloorsAsync(Agent, [0])).Outcome);
        Assert.Equal(CollectionsOutcome.Disabled, (await Floors(null).ListFloorsAsync(Agent, [1])).Outcome);
        Assert.Empty((await Floors(new Directory("TP124-P12")).ListFloorsAsync(Agent, null)).Value!.Floors);    // no apartment, no floor
    }
}
