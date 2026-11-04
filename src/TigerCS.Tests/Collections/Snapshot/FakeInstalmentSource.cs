using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>One instalment of the in-memory model. PaymentStatus is derived exactly like the SQL publish step (CollectionsPaymentFilters.Classify).</summary>
public sealed record FakeInstalment(int Company, string Tenant, string Name, int UnitId, string UnitCode, string Voucher, DateTime Due,
    decimal? Original, decimal? Allocated, decimal Remaining, string? SourceStatus, bool Breakdown, string Mobile = "", string Email = "")
{
    public string PaymentStatus => CollectionsPaymentFilters.Classify(Remaining, Original, Allocated, SourceStatus, Breakdown);
}

/// <summary>
/// In-memory reference model of dbo.usp_Collections_GetInstalmentsPage (same filters, totals, ordering and paging) over the snapshot status model.
/// Fidelity to the real T-SQL is checked against a real SQL Server by RealSqlSnapshotTests (not available in CI).
/// </summary>
public sealed class FakeInstalmentSource(DateTime nowUtc) : IPactReceivablesSource, IPactInstalmentSource, IPactInstalmentUnitSource, IPactInstalmentMonthSource
{
    public List<FakeInstalment> Rows { get; } = [];
    public List<CollectionsTowerDto> Towers { get; } = [new(1, "124", "Tower 124", 4, true), new(2, "136", "Tower 136", 4, true), new(3, "127", "Faradis", 32, true)];
    public Dictionary<int, SnapshotCompanyRaw> Companies { get; } = new()
    {
        [4] = Loaded(4, nowUtc.AddMinutes(-5), true, true), [32] = Loaded(32, nowUtc.AddMinutes(-5), true, true)
    };
    public PactInstalmentsRequest? LastRequest { get; private set; }
    public int Reads { get; private set; }

    public static SnapshotCompanyRaw Loaded(int company, DateTime successUtc, bool paidRetained, bool breakdown) =>
        FakeSnapshotSource.Healthy(company, successUtc) with { PaidRetained = paidRetained, BreakdownAvailable = breakdown };

    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Reference model of dbo.usp_Collections_GetInstalmentUnitsPage: the same filtered rows as the instalment view, grouped per (company, customer, unit) BEFORE paging.</summary>
    public Task<PactInstalmentUnitsPage> ReadInstalmentUnitsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        // Totals, filters and row mapping come from the instalment view over the WHOLE filtered set (no paging), then units are formed and paged.
        var all = ReadInstalmentsAsync(request with { Page = 1, PageSize = int.MaxValue / 2, Status = null }, cancellationToken).Result;
        if (all.Unavailable) return Task.FromResult(new PactInstalmentUnitsPage(all.Totals, [], true, all.Snapshot, all.ReadAtUtc, 0));
        // Status filter: the units that have at least one instalment of the class; the totals then cover exactly those units' instalments.
        var wanted = request.Status switch { "overdue" => "Overdue", "due" => "Due", _ => null };
        var kept = wanted is null ? all.Rows
            : all.Rows.GroupBy(r => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode)).Where(g => g.Any(r => r.Classification == wanted)).SelectMany(g => g).ToList();
        if (wanted is not null)
            all = all with { Totals = new PactInstalmentTotalsDto(kept.Count, kept.Sum(r => r.RemainingAmount),
                kept.Count(r => r.Classification == "Overdue"), kept.Where(r => r.Classification == "Overdue").Sum(r => r.RemainingAmount),
                kept.Count(r => r.Classification == "Due"), kept.Where(r => r.Classification == "Due").Sum(r => r.RemainingAmount),
                kept.Count(r => r.Classification == "NotYetDue"), kept.Where(r => r.Classification == "NotYetDue").Sum(r => r.RemainingAmount), all.Totals.FullyPaidCount) };
        // Minimum Total: the unit's SUM must be GREATER than it (never single instalments).
        if (request.MinTotal is { } floor)
        {
            kept = kept.GroupBy(r => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode)).Where(g => g.Sum(r => r.RemainingAmount) > floor).SelectMany(g => g).ToList();
            all = all with { Totals = new PactInstalmentTotalsDto(kept.Count, kept.Sum(r => r.RemainingAmount),
                kept.Count(r => r.Classification == "Overdue"), kept.Where(r => r.Classification == "Overdue").Sum(r => r.RemainingAmount),
                kept.Count(r => r.Classification == "Due"), kept.Where(r => r.Classification == "Due").Sum(r => r.RemainingAmount),
                kept.Count(r => r.Classification == "NotYetDue"), kept.Where(r => r.Classification == "NotYetDue").Sum(r => r.RemainingAmount), all.Totals.FullyPaidCount) };
        }
        var units = kept.GroupBy(r => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode))
            .Select(g => new PactInstalmentUnitDto(g.Key.CompanyId, ReceivablesSnapshotStatusBuilder.CompanyName(g.Key.CompanyId), g.First().TowerNumber, g.First().TowerName, g.Key.UnitId ?? 0,
                g.Key.UnitCode, g.Key.TenantId, g.First().CustomerName, g.Count(), g.Sum(r => r.RemainingAmount), g.Max(r => r.DueDate),
                g.OrderByDescending(r => r.DueDate).ThenBy(r => r.VoucherNumber, StringComparer.Ordinal).ToList()))
            .OrderByDescending(u => u.LatestDueDate).ThenBy(u => u.CompanyId).ThenBy(u => u.TenantId, StringComparer.Ordinal).ThenBy(u => u.UnitCode, StringComparer.Ordinal).ThenBy(u => u.UnitId).ToList();
        var page = units.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
        return Task.FromResult(new PactInstalmentUnitsPage(all.Totals with { UnitCount = units.Count }, page, false, all.Snapshot, all.ReadAtUtc, 1));
    }

    /// <summary>Reference model of dbo.usp_Collections_GetInstalmentMonths: the instalment view's filtered rows over the whole window, grouped by due month.</summary>
    public Task<IReadOnlyList<PactInstalmentMonthDto>> ReadInstalmentMonthsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        var (last, reads) = (LastRequest, Reads);
        var all = ReadInstalmentsAsync(request with { Page = 1, PageSize = int.MaxValue / 2 }, cancellationToken).Result;
        (LastRequest, Reads) = (last, reads);
        IReadOnlyList<PactInstalmentMonthDto> months = all.Unavailable ? [] : all.Rows.GroupBy(r => (r.DueDate.Year, r.DueDate.Month)).OrderBy(g => g.Key)
            .Select(g => new PactInstalmentMonthDto(g.Key.Year, g.Key.Month, g.Count(), g.Sum(r => r.RemainingAmount),
                g.Count(r => r.Classification == "Overdue"), g.Where(r => r.Classification == "Overdue").Sum(r => r.RemainingAmount))).ToList();
        return Task.FromResult(months);
    }

    public Task<PactInstalmentsPage> ReadInstalmentsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        Reads++; LastRequest = request;
        int? scope = request.CompanyId;
        // The chosen towers: TowerId plus TowerIds, one set (what the procedure does). Any unknown id invalidates the whole selection.
        var chosen = new List<CollectionsTowerDto>();
        foreach (var towerId in (request.TowerIds ?? []).Concat(request.TowerId is { } one ? new[] { one } : []).Distinct())
        {
            var tower = Towers.FirstOrDefault(t => t.TowerId == towerId);
            if (tower is null || (request.CompanyId is { } c && c != tower.CompanyId)) throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            chosen.Add(tower);
        }
        if (chosen.Select(t => t.CompanyId).Distinct().ToList() is [var onlyCompany]) scope = onlyCompany;
        var loaded = Companies.Values.Where(c => scope is null || c.CompanyId == scope).ToList();
        var status = ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, loaded, [], nowUtc, 90);
        var filter = request.PaymentFilter;
        var unavailable = (filter is "paid" or "all" && loaded.Any(c => c.HasSnapshot && !c.PaidRetained)) || (filter is "unpaid" or "partial" && loaded.Any(c => c.HasSnapshot && !c.BreakdownAvailable));
        var empty = new PactInstalmentTotalsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (unavailable) return Task.FromResult(new PactInstalmentsPage(empty, [], true, status, ReceivablesSnapshotComposer.ReadAt(status), 0));

        var min = filter is "paid" or "all" ? 0m : request.MinAmount;
        // By unit (ClassifyByDay) classifies against the CALENDAR MONTH of AsOf (today): Overdue before its first day, Due inside it, nothing from the next month on (CollectionsDueMonth).
        var monthStart = request.ClassifyByDay ? CollectionsDueMonth.MonthStart(request.AsOf) : request.AsOf; var nextMonth = monthStart.AddMonths(1);
        var rows = Rows.Where(r => loaded.Any(c => c.CompanyId == r.Company && c.HasSnapshot) && r.UnitId > 0 && r.UnitCode.Trim() is not ("" or "0") && !r.UnitCode.Contains('*'))
            .Where(r => chosen.Count == 0 || chosen.Any(t => t.CompanyId == r.Company && t.TowerNumber == CollectionsTowerNumber.Parse(r.UnitCode)))
            .Where(r => { var d = DateOnly.FromDateTime(r.Due); return d >= request.From && d <= request.To && (!request.ClassifyByDay || d < nextMonth); })
            .Where(r => filter switch
            {
                "outstanding" => r.Remaining > 0, "unpaid" => r.PaymentStatus == "Unpaid", "partial" => r.PaymentStatus == "PartiallyPaid",
                "paid" => r.PaymentStatus == "FullyPaid", _ => true
            })
            .Where(r => r.Remaining >= min)
            .Where(r => string.IsNullOrEmpty(request.Search) || new[] { r.Name, r.Tenant, r.UnitCode, r.Voucher, r.Mobile, r.Email, CollectionsTowerNumber.Parse(r.UnitCode) ?? "",
                Towers.FirstOrDefault(t => t.CompanyId == r.Company && t.TowerNumber == CollectionsTowerNumber.Parse(r.UnitCode))?.TowerName ?? "" }
                .Any(v => v.Contains(request.Search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        string Class(FakeInstalment r) { var d = DateOnly.FromDateTime(r.Due); return r.Remaining == 0 ? "NotApplicable" : d < monthStart ? "Overdue" : d < nextMonth ? "Due" : "NotYetDue"; }
        var totals = new PactInstalmentTotalsDto(rows.Count, rows.Sum(r => r.Remaining),
            rows.Count(r => r.Remaining > 0 && Class(r) == "Overdue"), rows.Where(r => r.Remaining > 0 && Class(r) == "Overdue").Sum(r => r.Remaining),
            rows.Count(r => r.Remaining > 0 && Class(r) == "Due"), rows.Where(r => r.Remaining > 0 && Class(r) == "Due").Sum(r => r.Remaining),
            rows.Count(r => r.Remaining > 0 && Class(r) == "NotYetDue"), rows.Where(r => r.Remaining > 0 && Class(r) == "NotYetDue").Sum(r => r.Remaining),
            rows.Count(r => r.Remaining == 0));
        var page = rows.OrderBy(r => r.Due).ThenBy(r => r.Company).ThenBy(r => r.UnitCode, StringComparer.Ordinal).ThenBy(r => r.Tenant, StringComparer.Ordinal).ThenBy(r => r.Voucher, StringComparer.Ordinal)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(r =>
            {
                var number = CollectionsTowerNumber.Parse(r.UnitCode);
                var tower = Towers.FirstOrDefault(t => t.CompanyId == r.Company && t.TowerNumber == number);
                var breakdownOk = r.PaymentStatus != "Unknown" && r.Breakdown;
                return new PactInstalmentRowDto(r.Company, ReceivablesSnapshotStatusBuilder.CompanyName(r.Company), number, tower?.TowerName, r.UnitId, r.UnitCode, r.Tenant, r.Name, r.Mobile, r.Email,
                    r.Voucher, "", DateOnly.FromDateTime(r.Due), breakdownOk ? r.Original : null, breakdownOk ? r.Allocated : null, r.Remaining, r.PaymentStatus, Class(r), r.SourceStatus ?? "");
            }).ToList();
        return Task.FromResult(new PactInstalmentsPage(totals, page, false, status, ReceivablesSnapshotComposer.ReadAt(status), 1));
    }
}
