using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

public sealed class PactReceivableCustomersAppService(
    CollectionsOptions collectionsOptions,
    PactReceivablesOptions sourceOptions,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    IPactReceivablesSource source,
    ILogger<PactReceivableCustomersAppService> logger,
    ICollectionsTowerCatalog? towerCatalog = null,
    IReceivablesRangeLoader? rangeLoader = null,
    IUnitFloorDirectory? floorDirectory = null)
{
    /// <summary>
    /// Asks for a background load of a due-date range the snapshot does not cover. Needs the same permission as reading the list;
    /// it triggers work, not data exposure, and the refresh itself is single-flight (a repeated click cannot start a second load).
    /// </summary>
    public async Task<CollectionsResult<ReceivablesRangeLoadDto>> RequestLoadAsync(
        CollectionsCaller caller, DateOnly from, DateOnly through, CancellationToken cancellationToken = default, int? companyId = null)
    {
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<ReceivablesRangeLoadDto>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled || rangeLoader is null)
            return CollectionsResult<ReceivablesRangeLoadDto>.Fail(CollectionsOutcome.Disabled);
        if (!CollectionsDateRanges.IsSupported(from, through))
            return CollectionsResult<ReceivablesRangeLoadDto>.Fail(CollectionsOutcome.InvalidRequest,
                "From date must not be after To date, and both must be within 2000-2100.");
        if (companyId is not (null or 4 or 32))
            return CollectionsResult<ReceivablesRangeLoadDto>.Fail(CollectionsOutcome.InvalidRequest, "Company must be 4 or 32.");
        try
        {
            var current = (await source.ReadAsync(new PactReceivablesRequest(from, through), cancellationToken)).Snapshot;
            // Retry of one company: its data may be complete but stale or behind a failed refresh ("ready" says nothing about that), so only a running load stops it.
            if (companyId is { } retry)
            {
                if (current is { LoadInProgress: true })
                    return CollectionsResult<ReceivablesRangeLoadDto>.Ok(new(false, false, true, "A load is already running. Reload this page in a few minutes."));
                var retried = await rangeLoader.EnqueueCompanyRefreshAsync(retry, cancellationToken);
                return CollectionsResult<ReceivablesRangeLoadDto>.Ok(retried
                    ? new(true, false, false, "Loading started in the background. Reload this page in a few minutes.")
                    : new(false, false, true, "A load is already running. Reload this page in a few minutes."));
            }
            if (current is { IsReady: true })
                return CollectionsResult<ReceivablesRangeLoadDto>.Ok(new(false, true, false, "The selected dates are already loaded and fresh."));
            if (current is { LoadInProgress: true })
                return CollectionsResult<ReceivablesRangeLoadDto>.Ok(new(false, false, true, "A load is already running. Reload this page in a few minutes."));
        }
        catch (PactReceivablesSourceException) { /* nothing loaded yet: a load is exactly what is needed */ }
        var started = await rangeLoader.EnqueueAsync(from, through, cancellationToken);
        return CollectionsResult<ReceivablesRangeLoadDto>.Ok(started
            ? new(true, false, false, "Loading started in the background. Reload this page in a few minutes.")
            : new(false, false, true, "A load is already running. Reload this page in a few minutes."));
    }

    /// <summary>
    /// The per-company refresh state only (no receivables): the small read the pages poll while a refresh is running. Same permission as reading the list.
    /// It never starts a refresh and never reads PACT.
    /// </summary>
    public async Task<CollectionsResult<SnapshotStatusDto>> GetSnapshotStatusAsync(CollectionsCaller caller, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<SnapshotStatusDto>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled || source is not ISnapshotStatusSource statusSource)
            return CollectionsResult<SnapshotStatusDto>.Fail(CollectionsOutcome.Disabled);
        try { return CollectionsResult<SnapshotStatusDto>.Ok(await statusSource.ReadStatusAsync(cancellationToken)); }
        catch (PactReceivablesSourceException ex) { return CollectionsResult<SnapshotStatusDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message); }
    }

    /// <summary>Active towers for the searchable dropdown (local table). Same authorization as the list itself.</summary>
    public async Task<CollectionsResult<IReadOnlyList<CollectionsTowerDto>>> ListTowersAsync(CollectionsCaller caller, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<IReadOnlyList<CollectionsTowerDto>>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled || towerCatalog is null)
            return CollectionsResult<IReadOnlyList<CollectionsTowerDto>>.Fail(CollectionsOutcome.Disabled);
        try
        {
            return CollectionsResult<IReadOnlyList<CollectionsTowerDto>>.Ok(await towerCatalog.ListActiveAsync(cancellationToken));
        }
        catch (PactReceivablesSourceException ex)
        {
            return CollectionsResult<IReadOnlyList<CollectionsTowerDto>>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// The floors of the apartment units of the chosen towers (every tower when none is named), for the Floor dropdown. Same authorization as the list. A unit that is not a recognisable apartment
    /// (see <see cref="CollectionsUnitFloor"/>) contributes nothing. Read from the local snapshot's unit directory; never from PACT.
    /// </summary>
    public async Task<CollectionsResult<CollectionsFloorsDto>> ListFloorsAsync(CollectionsCaller caller, IReadOnlyList<int>? towerIds, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<CollectionsFloorsDto>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled || floorDirectory is null)
            return CollectionsResult<CollectionsFloorsDto>.Fail(CollectionsOutcome.Disabled);
        var towers = (towerIds ?? []).Distinct().Order().ToList();
        if (towers.Any(id => id <= 0) || towers.Count > PactInstalmentsAppService.MaxTowers)
            return CollectionsResult<CollectionsFloorsDto>.Fail(CollectionsOutcome.InvalidRequest, "The selected towers are not valid. Choose towers from the list.");
        try
        {
            var codes = await floorDirectory.ListUnitCodesAsync(towers, cancellationToken);
            var floors = codes.Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(code => CollectionsUnitFloor.Parse(code)).OfType<int>()
                .GroupBy(floor => floor).OrderBy(g => g.Key)
                .Select(g => new CollectionsFloorDto(g.Key, CollectionsUnitFloor.Label(g.Key), g.Count())).ToList();
            return CollectionsResult<CollectionsFloorsDto>.Ok(new(floors, towers));
        }
        catch (PactReceivablesSourceException ex)
        {
            return CollectionsResult<CollectionsFloorsDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
    }

    public async Task<CollectionsResult<PactReceivableCustomersDto>> ListAsync(
        CollectionsCaller caller, int? companyId = null, string? status = null,
        string? search = null, int page = 1, int pageSize = 25,
        int? year = null, int? month = null, CancellationToken cancellationToken = default,
        int? towerId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, decimal? minAmount = null)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        // Authorize before reading configuration or any external customer data.
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.Disabled);
        var view = (status ?? "all").Trim().ToLowerInvariant();
        if (companyId is not (null or 4 or 32) || view is not ("all" or "due" or "overdue")
            || page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue
            || search?.Length > 200
            || (year is null) != (month is null) || year is < 2000 or > 2100 || month is < 1 or > 12
            || towerId is <= 0 || minAmount < 0 || minAmount > sourceOptions.MaxMinOutstandingAmount)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Choose a tower, a minimum amount of 0 or more, status All/Due/Overdue, page >= 1 and pageSize 1-100; year and month go together (2000-2100, 1-12); search is limited to 200 characters.");

        var today = clock.BusinessDate;
        // Reporting month (default: current Dubai month), matching EDSM's monthly receivables report:
        // OverDue = DueDate before the first day; Due = DueDate within the month, including later days.
        var reportYear = year ?? today.Year;
        var reportMonth = month ?? today.Month;
        var periodStart = new DateOnly(reportYear, reportMonth, 1);
        var periodEnd = new DateOnly(reportYear, reportMonth, DateTime.DaysInMonth(reportYear, reportMonth));
        // Instalment due-date window (inclusive; To covers its whole day). Default: the configured receivables start date through the
        // end of the reporting month. It only limits WHICH instalments are listed; the Due/Overdue classification below still
        // depends on the reporting month alone, so a window never turns a future instalment into an overdue one.
        var from = dateFrom ?? DateOnly.FromDateTime(sourceOptions.StartDate);
        var to = dateTo ?? periodEnd;
        if (!CollectionsDateRanges.IsSupported(from, to))
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.InvalidRequest,
                "From date must not be after To date, and both must be within 2000-2100.");
        // "Minimum outstanding amount": an instalment counts only when its remaining unpaid Amount >= min (inclusive).
        var min = decimal.Round(minAmount ?? sourceOptions.DefaultMinOutstandingAmount, 4);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(sourceOptions.RequestBudgetSeconds));
        if (source is IPactReceivablesPageSource pageSource)
            return await ListViaPageSourceAsync(pageSource, total, caller, companyId, view, search, page, pageSize, towerId, from, to, min, today,
                periodStart, periodEnd, reportYear, reportMonth, budget.Token, cancellationToken);
        PactReceivablesSnapshot snapshot;
        var sourceTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            snapshot = await source.ReadAsync(new PactReceivablesRequest(from, to, companyId, towerId, PactReceivableClass.DueOrOverdue, periodStart, min), budget.Token);
        }
        catch (PactReceivablesScopeException ex)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("PACT receivables read exceeded the {BudgetSeconds}s request budget.", sourceOptions.RequestBudgetSeconds);
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The PACT receivables read timed out. Please retry.");
        }
        catch (PactReceivablesSourceException ex)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("PACT receivables source failed ({ExceptionType}).", ex.GetType().Name);
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "PACT receivables could not be read. Please retry or contact support.");
        }

        // The only business filter: a positive Due or OverDue amount for the reporting month. Rows after the
        // month end are EDSM's "Outstanding" (not Due/OverDue) and are not part of this list.
        // Defensive repeat of the source filters: positive remaining balance, a valid unit (UnitID 0 is not actionable), the
        // window, and nothing after the reporting month.
        var sourceMs = sourceTimer.Elapsed.TotalMilliseconds;
        var live = snapshot.Items.Where(r => r.Amount > 0 && r.Amount >= min && r.UnitId is not <= 0
            && DateOnly.FromDateTime(r.DueDate) <= periodEnd
            && DateOnly.FromDateTime(r.DueDate) >= from && DateOnly.FromDateTime(r.DueDate) <= to).ToList();
        if (live.Any(r => r.CompanyId is not (4 or 32) || string.IsNullOrWhiteSpace(r.TenantId)))
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The source returned a receivable without a valid company/customer identity.");

        List<PactReceivableCustomerDto> customers;
        try
        {
            // One row per apartment: company + customer + unit.
            customers = live.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim(), r.UnitId, Unit: r.UnitCode.Trim()))
                .Select(g => Map(g.ToList(), today, periodStart, sourceOptions.SourceStatusMap)).ToList();
        }
        catch (OverflowException)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The PACT report contains amounts outside the supported range.");
        }
        var term = search?.Trim();
        if (companyId is { } company) customers = customers.Where(c => c.CompanyId == company).ToList();
        if (!string.IsNullOrWhiteSpace(term))
        {
            bool Match(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
            var digits = term.Where(char.IsDigit).ToArray();
            var phoneSearch = digits.Length >= 3 && !term.Any(char.IsLetter);
            customers = customers.Where(c => Match(c.FullName) || Match(c.TenantId) || Match(c.Email)
                || Match(c.Mobile) || c.Instalments.Any(i => Match(i.UnitCode))
                || (phoneSearch && new string(c.Mobile.Where(char.IsDigit).ToArray()).Contains(new string(digits), StringComparison.Ordinal)))
                .ToList();
        }
        customers = customers.Where(c => view == "all" || (view == "due" ? c.HasDue : c.HasOverdue))
            .OrderByDescending(c => c.HasOverdue).ThenBy(c => c.EarliestDueDate)
            .ThenBy(c => c.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.CompanyId).ThenBy(c => c.TenantId, StringComparer.Ordinal)
            .ThenBy(c => c.UnitCode, StringComparer.Ordinal).ThenBy(c => c.UnitId)
            .ToList();
        return CollectionsResult<PactReceivableCustomersDto>.Ok(new PactReceivableCustomersDto(
            today, reportYear, reportMonth, periodStart, periodEnd, snapshot.ReadAtUtc, snapshot.LegacyExclusionsApplied,
            snapshot.Snapshot?.Companies.Select(c => c.CompanyId).ToList() ?? [4, 32], sourceOptions.Currency, "Configured",
            customers.Count, customers.Count(c => c.HasDue), customers.Count(c => c.HasOverdue),
            page, pageSize, customers.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            from, to, towerId, snapshot.Snapshot, WindowNotes(from, to, periodStart, periodEnd), min,
            new ServerTimingsDto(sourceMs, total.Elapsed.TotalMilliseconds - sourceMs, total.Elapsed.TotalMilliseconds)));
    }

    /// <summary>
    /// SQL-backed path: filtering, per-apartment aggregation, counts and pagination run in the database, so only the requested page of
    /// apartments (with their instalments) is materialised here. Business rules are the same as the in-memory path below.
    /// </summary>
    private async Task<CollectionsResult<PactReceivableCustomersDto>> ListViaPageSourceAsync(
        IPactReceivablesPageSource pageSource, System.Diagnostics.Stopwatch total, CollectionsCaller caller, int? companyId, string view, string? search, int page, int pageSize,
        int? towerId, DateOnly from, DateOnly to, decimal min, DateOnly today, DateOnly periodStart, DateOnly periodEnd, int reportYear, int reportMonth,
        CancellationToken budgetToken, CancellationToken cancellationToken)
    {
        var term = search?.Trim();
        string? digits = null;
        if (!string.IsNullOrEmpty(term))
        {
            var d = new string(term.Where(char.IsDigit).ToArray());
            if (d.Length >= 3 && !term.Any(char.IsLetter)) digits = d;
        }
        PactReceivablesPage result;
        try
        {
            result = await pageSource.ReadPageAsync(new PactReceivablesPageRequest(from, to, periodStart, min, view, string.IsNullOrEmpty(term) ? null : term, digits, page, pageSize, companyId, towerId), budgetToken);
        }
        catch (PactReceivablesScopeException ex)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Local receivables read exceeded the {BudgetSeconds}s request budget.", sourceOptions.RequestBudgetSeconds);
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The receivables read timed out. Please retry.");
        }
        catch (PactReceivablesSourceException ex)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("Local receivables source failed ({ExceptionType}).", ex.GetType().Name);
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable, "Receivables could not be read. Please retry or contact support.");
        }

        var mapTimer = System.Diagnostics.Stopwatch.StartNew();
        var customers = result.Apartments.Select(a =>
        {
            var items = a.Instalments.Select(r => new PactReceivableInstalmentDto(r.UnitId, r.UnitCode, r.ProjectCode, r.VoucherNumber,
                r.ChequeNumber, DateOnly.FromDateTime(r.DueDate), r.Amount,
                DateOnly.FromDateTime(r.DueDate) < periodStart ? "OverDue" : "Due",
                PaymentStatus(r.SourceStatus, r.Amount, sourceOptions.SourceStatusMap),
                DateOnly.FromDateTime(r.DueDate) is var d && d < today ? "Overdue" : d == today ? "DueToday" : "Upcoming",
                r.SourceStatus ?? "", r.TowerNumber)).ToList();
            decimal? totalAmount = a.DueAmount is { } due && a.OverdueAmount is { } over ? due + over : null;
            return new PactReceivableCustomerDto(a.CompanyId, a.CompanyId == 4 ? "Tiger Group Dubai" : "Tiger Group Sharjah", a.TenantId, a.FullName, a.Mobile, a.Email,
                a.UnitId, a.UnitCode, a.ProjectCode, a.DueRows > 0, a.OverdueRows > 0, a.DueAmount, a.OverdueAmount, totalAmount,
                a.DueAmount is null || a.OverdueAmount is null ? "NeedsReview" : "Provided", a.Earliest, Math.Max(0, today.DayNumber - a.Earliest.DayNumber), items,
                a.TowerNumber, a.TowerName);
        }).ToList();
        var mapMs = mapTimer.Elapsed.TotalMilliseconds;
        return CollectionsResult<PactReceivableCustomersDto>.Ok(new PactReceivableCustomersDto(
            today, reportYear, reportMonth, periodStart, periodEnd, result.ReadAtUtc, false, result.Snapshot.Companies.Select(c => c.CompanyId).ToList(),
            sourceOptions.Currency, "Configured", result.TotalApartments, result.DueApartments, result.OverdueApartments, page, pageSize, customers,
            from, to, towerId, result.Snapshot, WindowNotes(from, to, periodStart, periodEnd), min,
            new ServerTimingsDto(result.SqlMs, mapMs, total.Elapsed.TotalMilliseconds)));
    }

    /// <summary>How the window and the reporting month interact, shown to the user next to the list.</summary>
    public static IReadOnlyList<string> WindowNotes(DateOnly from, DateOnly to, DateOnly periodStart, DateOnly periodEnd)
    {
        static string F(DateOnly d) => d.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var notes = new List<string>
        {
            $"Instalments due {F(from)} to {F(to)} (both days included) are listed. Overdue = due before {F(periodStart)}; Due = due {F(periodStart)} to {F(periodEnd)}."
        };
        if (to > periodEnd)
            notes.Add($"Instalments due after {F(periodEnd)} are neither Due nor Overdue and are not listed.");
        if (from > periodStart)
            notes.Add($"Due instalments before {F(from)} are outside the date range and are not included in the totals.");
        return notes;
    }

    /// <summary>
    /// Payment status from the verified remaining amount: 0 = Paid, more = Unpaid, missing or negative = Unknown. The report's <c>Status</c> only ever contradicts it: <c>Paid</c> (or a value
    /// mapped to Paid) with a remainder owed is Unknown. A mapping may refine a positive remainder to PartiallyPaid for display; it never decides paid or unpaid, and a due date never does.
    /// </summary>
    public static string PaymentStatus(string? source, decimal? remaining, IReadOnlyDictionary<string, string> map)
    {
        if (remaining is not { } amount || amount < 0) return "Unknown";
        var value = source?.Trim() ?? "";
        var mapped = map.TryGetValue(value, out var m) ? m.Trim().ToLowerInvariant() : null;
        var saysPaid = value.Equals("Paid", StringComparison.OrdinalIgnoreCase) || mapped == "paid";
        if (amount == 0) return "Paid";
        if (saysPaid) return "Unknown";                                                              // contradictory: the source says Paid but a remainder is owed
        return mapped == "partiallypaid" ? "PartiallyPaid" : "Unpaid";
    }

    private static PactReceivableCustomerDto Map(List<PactReceivableInstalment> rows, DateOnly today, DateOnly periodStart,
        IReadOnlyDictionary<string, string> statusMap)
    {
        var first = rows[0];
        var items = rows.OrderBy(r => r.DueDate).ThenBy(r => r.UnitCode, StringComparer.Ordinal)
            .Select(r => new PactReceivableInstalmentDto(r.UnitId, r.UnitCode, r.ProjectCode, r.VoucherNumber,
                r.ChequeNumber, DateOnly.FromDateTime(r.DueDate), r.Amount,
                DateOnly.FromDateTime(r.DueDate) < periodStart ? "OverDue" : "Due",
                PaymentStatus(r.SourceStatus, r.Amount, statusMap),
                DateOnly.FromDateTime(r.DueDate) is var d && d < today ? "Overdue" : d == today ? "DueToday" : "Upcoming",
                r.SourceStatus ?? "", r.TowerNumber)).ToList();
        // These SPs join invoice rows to payment terms by tenant only. Multiple returned
        // rows on one date can be duplicated allocations OR legitimate instalments.
        // Preserve the rows; never silently DISTINCT them or sum an ambiguous date.
        var due = items.Where(i => i.ReceivablesType == "Due").ToList();
        var overdue = items.Where(i => i.ReceivablesType == "OverDue").ToList();
        static decimal? SumIfUnambiguous(List<PactReceivableInstalmentDto> bucket) =>
            bucket.GroupBy(i => i.DueDate).Any(g => g.Count() > 1) ? null : bucket.Sum(i => i.RemainingAmount);
        var dueAmount = SumIfUnambiguous(due);
        var overdueAmount = SumIfUnambiguous(overdue);
        // Due (within the month) and OverDue (before the month) are disjoint by date, so no source row
        // can sit in both buckets; the total is therefore a plain sum, and only when both are known.
        decimal? totalAmount = dueAmount is { } d && overdueAmount is { } o ? d + o : null;
        var earliest = items.Min(i => i.DueDate);
        return new PactReceivableCustomerDto(first.CompanyId,
            first.CompanyId == 4 ? "Tiger Group Dubai" : "Tiger Group Sharjah", first.TenantId.Trim(),
            first.FullName, first.Mobile, first.Email, first.UnitId, first.UnitCode.Trim(), first.ProjectCode,
            due.Count > 0, overdue.Count > 0,
            dueAmount, overdueAmount, totalAmount, dueAmount is null || overdueAmount is null ? "NeedsReview" : "Provided",
            earliest, Math.Max(0, today.DayNumber - earliest.DayNumber), items, first.TowerNumber, first.TowerName);
    }
}
