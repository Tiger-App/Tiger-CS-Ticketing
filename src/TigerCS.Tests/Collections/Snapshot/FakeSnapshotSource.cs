using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>
/// In-memory reference model of dbo.usp_Collections_GetReceivables + SnapshotPactReceivablesSource (same filters, same
/// composition through <see cref="ReceivablesSnapshotComposer"/>). The T-SQL itself needs SQL Server; see
/// database/collections-receivables/tests/smoke_snapshot_publish_and_read.sql.
/// </summary>
public sealed class FakeSnapshotSource(DateTime nowUtc) : IPactReceivablesSource
{
    public List<PactReceivableInstalment> Rows { get; } = [];
    public List<CollectionsTowerDto> Towers { get; } = [];
    public Dictionary<int, SnapshotCompanyRaw> Companies { get; } = new()
    {
        [4] = Healthy(4, nowUtc.AddMinutes(-10)),
        [32] = Healthy(32, nowUtc.AddMinutes(-10))
    };
    public int Reads { get; private set; }
    public PactReceivablesRequest? LastRequest { get; private set; }
    public int MaxAgeMinutes { get; set; } = 90;
    /// <summary>The clock the age of a company's snapshot is judged against (a test that moves its clock sets it).</summary>
    public Func<DateTime> NowUtc { get; set; } = () => nowUtc;

    public static SnapshotCompanyRaw Healthy(int company, DateTime successUtc) => new(company, true, successUtc, successUtc, "Succeeded", null, 0, 10,
        new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), 0, 0m, 0, 0m, 0, 0);

    public static SnapshotCompanyRaw Failed(int company, DateTime lastSuccessUtc, DateTime attemptUtc, int errorNumber = 7416) =>
        Healthy(company, lastSuccessUtc) with { LastAttemptUtc = attemptUtc, LastAttemptStatus = "Failed", LastErrorNumber = errorNumber, ConsecutiveFailures = 3 };

    public static SnapshotCompanyRaw NeverLoaded(int company) => new(company, false, null, null, "Never", null, 0, 0, null, null, 0, 0m, 0, 0m, 0, 0);

    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
        ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);

    public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
    {
        Reads++; LastRequest = request;
        int? scope = request.CompanyId;
        string? towerNumber = null;
        if (request.TowerId is { } towerId)
        {
            var tower = Towers.FirstOrDefault(t => t.TowerId == towerId);
            if (tower is null || (request.CompanyId is { } c && c != tower.CompanyId))
                throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            scope = tower.CompanyId; towerNumber = tower.TowerNumber;
        }
        var from = request.FromDate ?? new DateOnly(2000, 1, 1);
        var asOf = request.AsOfDate ?? request.ThroughDate;
        var monthStart = new DateOnly(asOf.Year, asOf.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var loaded = Companies.Values.Where(c => scope is null || c.CompanyId == scope).ToList();
        var rows = Rows.Where(r => loaded.Any(c => c.CompanyId == r.CompanyId && c.HasSnapshot))
            .Where(r => r.Amount > 0 && r.UnitId is > 0)
            .Select(r => WithTower(r))
            .Where(r => towerNumber is null || r.TowerNumber == towerNumber)
            .Where(r => { var d = DateOnly.FromDateTime(r.DueDate); return d >= from && d <= request.ThroughDate; })
            .Where(r =>
            {
                var d = DateOnly.FromDateTime(r.DueDate);
                return request.Class switch
                {
                    PactReceivableClass.DueOrOverdue => d < nextMonth,
                    PactReceivableClass.Due => d >= monthStart && d < nextMonth,
                    PactReceivableClass.Overdue => d < monthStart,
                    _ => true
                };
            }).ToList();
        var unmatched = Rows.Where(r => loaded.Any(c => c.CompanyId == r.CompanyId && c.HasSnapshot))
            .Select(r => WithTower(r)).Where(r => r.TowerNumber is null || r.TowerId is null)
            .GroupBy(r => (r.CompanyId, r.TowerNumber))
            .Select(g => new UnmatchedTowerDto(g.Key.CompanyId, g.Key.TowerNumber, g.Key.TowerNumber is null ? "NoTowerNumber" : "NoMatchingTower", g.Count(), g.Sum(x => x.Amount))).ToList();
        return Task.FromResult(ReceivablesSnapshotComposer.Compose(from, request.ThroughDate, loaded, unmatched, rows, NowUtc(), MaxAgeMinutes));
    }

    private PactReceivableInstalment WithTower(PactReceivableInstalment row)
    {
        var number = CollectionsTowerNumber.Parse(row.UnitCode);
        var tower = Towers.FirstOrDefault(t => t.CompanyId == row.CompanyId && CollectionsTowerNumber.Matches(number, t.TowerNumber));
        return row with { TowerNumber = number, TowerId = tower?.TowerId, TowerName = tower?.TowerName };
    }
}
