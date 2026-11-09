using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>Read-only campaign preview and manual export. No scheduling, dispatch, ticket creation or legal referral.</summary>
public sealed class CollectionsCampaignAppService(
    CollectionsOptions options, CollectionsCampaignOptions campaignOptions, PactReceivablesOptions sourceOptions,
    CollectionsAuthorizationService authorization, CollectionsClock clock, IPactReceivablesSource source,
    ILogger<CollectionsCampaignAppService> logger, CollectionsUnitLinkService? links = null,
    IFinancialReconciliationEvidence? reconciliation = null)
{
    /// <summary>First day the receivables history is read from when the user gives no From date (the snapshot window starts here).</summary>
    public static readonly DateOnly HistoryStart = new(2000, 1, 1);

    public async Task<CollectionsResult<CollectionsCampaignPreviewDto>> PreviewAsync(
        CollectionsCaller caller, string? stage, DateOnly? businessDate = null, int? companyId = null,
        string? search = null, int page = 1, int pageSize = 25, bool forExport = false,
        CancellationToken cancellationToken = default, DateOnly? dateFrom = null, DateOnly? dateTo = null, int? towerId = null, decimal? minTotal = null,
        string? paymentStatus = null, IReadOnlyList<int>? towerIds = null, string? sendingMode = null, IReadOnlyList<int>? floors = null)
    {
        var total = Stopwatch.StartNew();
        // Project / Tower filter: any number of local tower ids (the single towerId is kept for old callers). None = All projects. One tower keeps the
        // established single-tower paths; several are read tower by tower through the same in-memory evaluation, so counts, totals and pages stay exact.
        var towers = (towerIds ?? []).Concat(towerId is { } single ? new[] { single } : Array.Empty<int>()).Distinct().Order().ToList();
        towerId = towers.Count == 1 ? towers[0] : null;
        var multiTower = towers.Count > 1;
        // Floor filter (apartment floors derived from the unit code, see CollectionsUnitFloor): none = All floors = no filtering. It only NARROWS the finished list of units, so a unit shows exactly the
        // figures, flags and status it has without the filter, and the counts, totals, instalments, pages and both exports agree.
        var floorList = CollectionsUnitFloor.Normalize(floors);
        var floorSet = floorList.Count == 0 ? null : floorList.ToHashSet();
        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanReadFinancials || (forExport && !permissions.CanSendReminders))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled || !sourceOptions.Enabled)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.Disabled);
        var date = businessDate ?? clock.BusinessDate;
        if (!CollectionsSendingModes.TryParse(sendingMode, out var mode))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest, "Sending mode must be Manual or Auto.");
        // This is the Auto (policy schedule) preview. A Manual list is built from the stored review data, with the amount calculated from the person's filters; it never comes from here.
        if (mode == CollectionsSendingModes.Manual)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest, "A Manual list comes from the stored review data, not from the schedule preview.");
        if (!CampaignPaymentFilters.TryParse(paymentStatus, out var paymentFilter))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest, "Payment status must be Due, Overdue or Both.");
        if (!CollectionsEnums.TryParse<CollectionsCampaignStage>(stage, out var selected)
            || date.Year is < 2000 or > 2100 || companyId is not (null or 4 or 32)
            || search?.Length > 200 || page < 1 || pageSize is < 1 or > 100 || towers.Any(t => t <= 0) || CollectionsUnitFloor.HasInvalid(floors) || minTotal < 0 || minTotal > sourceOptions.MaxMinOutstandingAmount
            || (long)(page - 1) * pageSize > int.MaxValue)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Choose a campaign stage, a date in 2000-2100, company 4 or 32, a minimum amount of 0 or more, valid floors (0-99), page >= 1 and pageSize 1-100; search is limited to 200 characters.");
        // "Minimum Total": a unit is listed only when its Due + Overdue is GREATER than this (null = no minimum). It is applied to the unit's sum - never to single
        // instalments - before the counts, the pages and both CSV exports, so all of them agree.
        decimal? unitMinTotal = minTotal is { } requested ? decimal.Round(requested, 4) : null;
        const decimal min = 0m;   // no per-instalment minimum any more
        var today = clock.BusinessDate;   // the real Dubai date; its CALENDAR MONTH is the reference (CollectionsDueMonth): Overdue = before its first day, Due = inside it; nothing from the next month on is listed

        // Instalment due-date window. Default To: the end of the preview month for every stage, so the days still to come in the current month are never cut off of the
        // Due figure. It is clamped to the end of the CURRENT Dubai month: instalments due from the next month on are neither Due nor Overdue and are not part of any list,
        // total or export. The preview date still alone drives stage scheduling/eligibility; the window only limits which instalments are read.
        var to = CollectionsDueMonth.ClampTo(dateTo ?? CollectionsCampaignPolicy.DefaultDateTo(selected, date), today);
        // TWO scopes, deliberately separate:
        //  * ELIGIBILITY scope (stage policy, unchanged): instalments from the explicit From, else the configured receivables StartDate (not 1 January of the preview year:
        //    overdue and legal stages look back months). It decides WHICH units are candidates, their stage amount, flags and review reasons.
        //  * READ scope (what the list shows): the explicit From, else the whole history (2000-01-01) - "All months" reaches previous years up to today, like Receivables.
        //    It only feeds the Due / Overdue / Total columns, the Minimum Total and the ordering; widening it never makes a unit eligible or changes a stage amount.
        var configuredStart = DateOnly.FromDateTime(sourceOptions.StartDate);
        if (dateFrom is null && configuredStart > to)
            // No calendar-year fallback: instalments before the configured start date are out of the stage policy, so a window that ends
            // before it has nothing eligible.
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"The window ends before the configured receivables start date ({configuredStart:yyyy-MM-dd}); nothing earlier is in scope. Choose a later preview date, or set From date explicitly.");
        var eligibleFrom = dateFrom ?? configuredStart;
        var from = dateFrom ?? HistoryStart;
        if (!CollectionsDateRanges.IsSupported(from, to))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                "From date must not be after To date (instalments due from the next month on are not listed), and both must be within 2000-2100.");

        // Search: the same term semantics for both evaluation paths (name, customer id, unit code, normalised phone / e-mail, phone digits).
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var digits = term is null ? "" : new string(term.Where(char.IsAsciiDigit).ToArray());
        var phoneDigits = term is not null && digits.Length >= 3 && !term.Any(char.IsLetter) ? digits : null;
        var (stageFrom, stageToExclusive) = CollectionsCampaignPolicy.StageRange(selected, date);
        var exportLimit = Math.Clamp(campaignOptions.MaxExportRows, 1, 50000);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(sourceOptions.RequestBudgetSeconds));
        PactCampaignPage? sqlPage = null;
        PactReceivablesSnapshot? snapshot = null;
        var started = Stopwatch.GetTimestamp();
        var scope = companyId is { } c ? $"company {c}" : "companies 4 and 32";
        try
        {
            // The data store evaluates the campaign itself when it can (filtering, aggregation, review flags, totals, paging): only the requested
            // page - or, for an export, the bounded full result - reaches the application. Otherwise (a source without that capability, or a
            // snapshot the engine cannot use) every instalment of the window is read and evaluated in memory, as before.
            // A single payment status is evaluated in memory (the reference implementation): an engine that does not know the filter must never answer for it.
            if (source is IPactCampaignSource campaign && paymentFilter == CampaignPaymentFilter.Both && !multiTower && floorSet is null)
            {
                var request = new PactCampaignRequest(from, to, min, stageFrom, stageToExclusive, CollectionsCampaignPolicy.Threshold(selected),
                    selected != CollectionsCampaignStage.LegalReferral, term, phoneDigits,
                    forExport ? 0 : (page - 1) * pageSize, forExport ? exportLimit + 1 : pageSize, companyId, towerId, today, unitMinTotal, paymentFilter);
                sqlPage = await campaign.ReadCampaignAsync(request, budget.Token);
                if (!sqlPage.Supported) sqlPage = null;
            }
            // Company and window are pushed down to the source: a single-company request must not wait for the other
            // company's (much slower) procedure, and rows outside the window are never read.
            if (sqlPage is null && multiTower)
            {
                PactReceivablesSnapshot? merged = null;
                var rows = new List<PactReceivableInstalment>();
                foreach (var tower in towers)
                {
                    var part = await source.ReadAsync(new PactReceivablesRequest(from, to, companyId, tower, MinAmount: min), budget.Token);
                    merged ??= part;
                    rows.AddRange(part.Items);
                }
                snapshot = merged! with { Items = rows };
            }
            else if (sqlPage is null)
                snapshot = await source.ReadAsync(new PactReceivablesRequest(from, to, companyId, towerId, MinAmount: min), budget.Token);
        }
        catch (PactReceivablesScopeException ex)
        {
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Campaign preview {Stage} ({Scope}) was cancelled by the caller after {ElapsedMs} ms (client disconnected).",
                selected, scope, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Campaign preview {Stage} ({Scope}, {From:yyyy-MM-dd}..{To:yyyy-MM-dd}) exceeded the {Budget} s request budget after {ElapsedMs} ms; the caller was still waiting.",
                selected, scope, from, to, sourceOptions.RequestBudgetSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The PACT receivables read timed out. Please retry.");
        }
        catch (PactReceivablesSourceException ex)
        {
            logger.LogWarning("Campaign preview {Stage} ({Scope}) source failure after {ElapsedMs} ms: {Reason}.",
                selected, scope, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("Campaign preview {Stage} ({Scope}) source read failed after {ElapsedMs} ms ({ExceptionType}).",
                selected, scope, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.GetType().Name);
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The campaign source could not be read.");
        }

        var sourceMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var snapshotStatus = sqlPage?.Snapshot ?? snapshot!.Snapshot;
        // The CRM side of the contacts. ONE bulk read for the whole result (never a request per unit); when CRM linking is switched on but unusable, every unit says so
        // (CrmUnavailable) instead of looking like a unit CRM simply holds no mobile for.
        var crmSet = CrmLinkSet.None;
        if (links is not null)
            crmSet = sqlPage is not null
                ? new CrmLinkSet(await links.GetAvailabilityAsync(budget.Token), CrmLinkSet.None.Links)
                : await links.GetLinksAsync(snapshot!.Items.Select(r => (r.CompanyId, r.UnitCode)), budget.Token);
        var crmUnavailableFlag = crmSet.Availability.Blocks ? CollectionsCampaignFlags.CrmUnavailable : CollectionsCampaignFlags.None;
        var readAtUtc = sqlPage?.ReadAtUtc ?? snapshot!.ReadAtUtc;
        logger.LogInformation("Campaign preview {Stage} ({Scope}, {From:yyyy-MM-dd}..{To:yyyy-MM-dd}) read {Rows} {What} in {ElapsedMs} ms ({Engine}).",
            selected, scope, from, to, sqlPage?.Units.Count ?? snapshot!.Items.Count, sqlPage is null ? "source rows" : "units", sourceMs, sqlPage is null ? "in memory" : "sql");

        // ONE definition of "reconciled" (sign-off + procedure + recent automated evidence), shared with the stored review data.
        var gate = SourceGate.Evaluate(campaignOptions, sourceOptions, reconciliation, clock.UtcNow);
        var dates = CollectionsCampaignPolicy.ScheduledDates(selected, date);
        var cycle = $"{date.ToString("yyyy-MM", CultureInfo.InvariantCulture)}:{selected}";
        // Local snapshot: every company in scope must be loaded and within the documented maximum age
        // (ReceivablesSnapshotOptions.MaxAgeMinutes). Other sources keep the generic staleness test.
        var fresh = snapshotStatus is { } status
            ? status.IsFresh
            : readAtUtc.Kind == DateTimeKind.Utc && !clock.IsStale(readAtUtc) && readAtUtc <= clock.UtcNow.AddMinutes(1);
        var coverageIncomplete = snapshotStatus is { RangeCovered: false };
        // The gate per company (one company's missing evidence never holds the other back) and the units a reconciled company holds back one by one.
        var heldUnits = gate.HeldUnitKeys();
        var scopeReconciled = companyId is { } scopedCompany ? gate.IsOpen(scopedCompany) : gate.Reconciled;
        CollectionsCampaignContactDto ToContact(CampaignUnitFacts f)
        {
            var (unitStatus, reasons) = Compose(f.Flags | (int)crmUnavailableFlag, fresh, coverageIncomplete, gate.IsOpen(f.CompanyId), selected, date, dates);
            if (heldUnits.Count > 0 && heldUnits.Contains((f.CompanyId, f.TenantId.Trim(), (long)(f.UnitId ?? 0))) && unitStatus is "Ready" or "PreviewOnly" or "NeedsReview")
            { unitStatus = "NeedsReview"; reasons = [.. reasons.Where(r => r != "OutsideSchedule"), "SourceMismatchUnresolved"]; }
            var identity = $"{f.CompanyId}:{f.TenantId}:{f.UnitId}:{f.UnitCode}:{cycle}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return new CollectionsCampaignContactDto(id, $"ext:Pact:{f.TenantId}", f.CompanyId, f.TenantId, f.FullName, f.Phone, f.Email,
                f.UnitId, f.UnitCode, f.ProjectCode, f.Amount, sourceOptions.Currency, f.EarliestDue, selected.ToString(), cycle,
                unitStatus, string.Join(";", reasons.Count == 0 ? ["Qualifies"] : reasons), f.TowerNumber, f.TowerName, f.DueAmount, f.OverdueAmount, f.CrmCustomerId);
        }

        int count, ready, review, previewOnly = 0, notEligible = 0;
        decimal? eligibleTotal = null, eligibleOutstanding = null;
        List<CollectionsCampaignContactDto> items;
        if (sqlPage is not null)
        {
            if (sqlPage.BadIdentityRows > 0)
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                    "PACT returned a receivable without a valid company/customer identity.");
            if (forExport && sqlPage.Total > exportLimit)
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                    "The export exceeds the row limit. Filter by company or customer; no partial file was created.");
            // Every unit-level reason makes a unit NeedsReview whatever the global gates are; a unit without one gets the status a hypothetical clean
            // unit would get, so Ready / NeedsReview counts are exact over the whole filtered set without materialising it.
            var cleanStatus = Compose((int)crmUnavailableFlag, fresh, coverageIncomplete, scopeReconciled, selected, date, dates).Status;
            count = sqlPage.Total;
            ready = cleanStatus == "Ready" ? sqlPage.Clean : 0;
            review = sqlPage.Review + (cleanStatus == "NeedsReview" ? sqlPage.Clean : 0);
            previewOnly = cleanStatus == "PreviewOnly" ? sqlPage.Clean : 0;
            items = sqlPage.Units.Select(ToContact).ToList();
        }
        else
        {
            List<CampaignUnitFacts> facts;
            try
            {
                facts = BuildFactsInMemory(snapshot!.Items, from, to, min, selected, date, term, phoneDigits, companyId, today, unitMinTotal, crmSet.Links, eligibleFrom, crmSet.Availability.Blocks, paymentFilter, floorSet);
            }
            catch (OverflowException)
            {
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                    "The campaign contains amounts outside the supported range.");
            }
            catch (InvalidDataException)
            {
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                    "PACT returned a receivable without a valid company/customer identity.");
            }
            catch (Exception ex) when (ex is DbException)
            {
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The CRM owner data could not be read.");
            }
            if (forExport && facts.Count > exportLimit)
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                    "The export exceeds the row limit. Filter by company or customer; no partial file was created.");
            var contacts = facts.Select(ToContact).ToList();
            count = contacts.Count;
            ready = contacts.Count(x => x.Status == "Ready");
            review = contacts.Count(x => x.Status == "NeedsReview");
            notEligible = contacts.Count(x => x.Status == "NotEligible");
            previewOnly = contacts.Count(x => x.Status == "PreviewOnly");
            // The qualifying amount: Due + Overdue of the units the stage rule makes eligible, over the WHOLE filtered list (not just this page).
            // Total qualifying amount = the STAGE amount of every eligible unit over the whole filtered list: exactly what Review & approve shows and what Genesys receives as AmountDue.
            // It is not Due + Overdue (the balance columns): a stage amount is the part of the balance the stage rule selects. That balance is reported separately as the outstanding figure.
            eligibleTotal = contacts.Where(x => x.Status != "NotEligible").Sum(x => x.Amount ?? 0m);
            eligibleOutstanding = contacts.Where(x => x.Status != "NotEligible").Sum(x => x.TotalAmount);
            items = forExport ? contacts : contacts.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }
        return CollectionsResult<CollectionsCampaignPreviewDto>.Ok(new(date, clock.BusinessDate, readAtUtc,
            "PACT receivables (companies 4 and 32)", selected.ToString(), cycle, dates, dates.Contains(date),
            scopeReconciled, sqlPage is null && snapshot!.LegacyExclusionsApplied, permissions.CanSendReminders,
            count, ready, review, page, pageSize, items,
            from, to, CollectionsCampaignPolicy.RangeNotes(selected, date, eligibleFrom, to), towerId, snapshotStatus, min,
            new ServerTimingsDto(sourceMs, total.Elapsed.TotalMilliseconds - sourceMs, total.Elapsed.TotalMilliseconds), CrmContactStatusDto.From(crmSet.Availability), notEligible,
            CampaignPaymentFilters.Name(paymentFilter), eligibleTotal, eligibleOutstanding, PaymentStatusNote(paymentFilter, dateFrom, to, today), gate, towers.Count == 0 ? null : towers,
            mode, previewOnly, campaignOptions.AutoSendingEnabled, floorList.Count == 0 ? null : floorList));
    }

    /// <summary>
    /// How the Due / Overdue status meets the chosen dates. Due = instalments due inside the current Dubai month, Overdue = due before its first day; both are measured against TODAY
    /// (not the preview date), and the dates only choose which instalments are read ([From, To], To never past the end of the current month). A combination that cannot match anything says so.
    /// </summary>
    internal static string? PaymentStatusNote(CampaignPaymentFilter filter, DateOnly? from, DateOnly to, DateOnly today)
    {
        var monthStart = CollectionsDueMonth.MonthStart(today);
        if (filter == CampaignPaymentFilter.Due && to < monthStart)
            return $"Due means due inside the current month (from {monthStart:yyyy-MM-dd}); the chosen dates end before it, so nothing is Due.";
        if (filter == CampaignPaymentFilter.Overdue && from is { } start && start >= monthStart)
            return $"Overdue means due before {monthStart:yyyy-MM-dd}; the chosen dates start on or after it, so nothing is Overdue.";
        return null;
    }

    /// <summary>Review reasons and status of a candidate from its unit-level flags and the global (source / schedule / release) gates. Order is part of the contract.</summary>
    private (string Status, List<string> Reasons) Compose(int flags, bool fresh, bool coverageIncomplete, bool sourceReconciled, CollectionsCampaignStage selected,
        DateOnly date, IReadOnlyList<DateOnly> dates)
    {
        bool Has(CollectionsCampaignFlags flag) => (flags & (int)flag) != 0;
        // The stage rule found nothing to send for this unit: it stays listed with its real balances; only its readiness says NotEligible.
        if (Has(CollectionsCampaignFlags.NoQualifyingBalance)) return ("NotEligible", ["NoQualifyingBalance"]);
        if (Has(CollectionsCampaignFlags.BelowThreshold)) return ("NotEligible", ["BelowThreshold"]);
        var reasons = new List<string>();
        if (Has(CollectionsCampaignFlags.AmbiguousInstalments)) reasons.Add("AmbiguousInstalments");
        if (Has(CollectionsCampaignFlags.AmountPrecisionNeedsReview)) reasons.Add("AmountPrecisionNeedsReview");
        if (Has(CollectionsCampaignFlags.MissingUnitIdentity)) reasons.Add("MissingUnitIdentity");
        if (Has(CollectionsCampaignFlags.ConflictingContactDetails)) reasons.Add("ConflictingContactDetails");
        if (Has(CollectionsCampaignFlags.UnitAllocationNeedsReview)) reasons.Add("UnitAllocationNeedsReview");
        if (Has(CollectionsCampaignFlags.ContradictoryPaymentStatus)) reasons.Add("ContradictoryPaymentStatus");
        if (sourceOptions.Currency != "AED") reasons.Add("CurrencyNeedsReview");
        if (!fresh) reasons.Add("StaleSource");
        if (coverageIncomplete) reasons.Add("CoverageIncomplete");
        // The reconciliation gate is company-wide (FinancialSourceValidated / SourceGate), never a per-unit reason: a unit's own status says what is wrong with the unit. The one per-unit
        // case is the unit's own data: a unit whose snapshot differs persistently from PACT is marked SourceMismatchUnresolved by the caller.
        if (Has(CollectionsCampaignFlags.NoValidContact)) reasons.Add("NoValidContact");
        if (Has(CollectionsCampaignFlags.CrmCustomerAmbiguous)) reasons.Add("CrmCustomerAmbiguous");
        if (Has(CollectionsCampaignFlags.CrmCompanyAmbiguous)) reasons.Add("CrmCompanyAmbiguous");
        if (Has(CollectionsCampaignFlags.CrmUnavailable)) reasons.Add("CrmUnavailable");
        var status = reasons.Count > 0 ? "NeedsReview" : "Ready";
        if (reasons.Count == 0 && (date != clock.BusinessDate || !dates.Contains(date)))
        { status = "PreviewOnly"; reasons.Add("OutsideSchedule"); }
        if (reasons.Count == 0 && selected == CollectionsCampaignStage.LegalNotice && !campaignOptions.LegalNoticeExportEnabled)
        { status = "NeedsReview"; reasons.Add("LegalNoticeReleaseRequired"); }
        if (reasons.Count == 0 && selected == CollectionsCampaignStage.LegalReferral)
        { status = "InternalReview"; reasons.Add("InternalLegalReferralOnly"); }
        return (status, reasons);
    }

    /// <summary>
    /// The in-memory evaluation: every instalment of the window is grouped per unit here. It is the reference implementation of the rules (the SQL engine
    /// must return the same units, flags, totals and order - see the equivalence tests) and the fallback for sources/snapshots without the SQL engine.
    /// </summary>
    internal List<CampaignUnitFacts> BuildFactsInMemory(IReadOnlyList<PactReceivableInstalment> items, DateOnly from, DateOnly to, decimal min,
        CollectionsCampaignStage selected, DateOnly date, string? term, string? phoneDigits, int? companyId, DateOnly? today = null, decimal? minTotal = null,
        IReadOnlyDictionary<(int CompanyId, string UnitKey), CrmUnitLink>? crmLinks = null, DateOnly? eligibleFrom = null, bool crmUnavailable = false,
        CampaignPaymentFilter paymentFilter = CampaignPaymentFilter.Both, IReadOnlySet<int>? floors = null)
    {
        // READ scope = [from, to]; ELIGIBILITY scope = [eligibleFrom ?? from, to]. A unit is a candidate, and its stage amount and unit-level flags are decided, ONLY from its
        // eligibility rows - exactly as before the read scope was widened; the read scope adds nothing but the Due / Overdue / Total figures and the ordering date.
        var eligibleStart = eligibleFrom ?? from;
        // Defensive: a legacy source may ignore the window.
        // Floating-point residue from the deployed procedures (e.g. 1E-12 on a settled instalment) is snapped to fils; a genuine
        // sub-fils amount stays as read so AmountPrecisionNeedsReview still fires. Rounding is never used to hide it.
        var rows = items
            .Select(r => MoneyNormalizer.Normalize(r.Amount, r.AmountIsFloatingPoint) is { IsResolved: true, Value: { } value } ? r with { Amount = value } : r)
            .Where(r => r.Amount > 0 && r.Amount >= min && DateOnly.FromDateTime(r.DueDate) >= from && DateOnly.FromDateTime(r.DueDate) <= to
                && !r.UnitCode.Contains('*')    // a '*' in the unit code marks a cancelled apartment (e.g. 513*): excluded before any grouping
                && r.UnitId is > 0 && r.UnitCode.Trim() is not ("" or "0"))   // units without a real number are never listed
            .ToList();
        if (rows.Any(r => r.CompanyId is not (4 or 32) || string.IsNullOrWhiteSpace(r.TenantId)))
            throw new InvalidDataException("PACT returned a receivable without a valid company/customer identity.");
        // The originals can repeat one instalment under different apartments of the same tenant.
        // ELIGIBILITY is decided by the stage rule alone (CollectionsCampaignPolicy.StageRange via Evaluate/Qualifying) - the SAME call Review makes - over the rows of the eligibility
        // scope [eligibleStart, to]. There is deliberately no extra "up to today" cut here any more: for the Current-month and Follow-up stages the stage window is the whole preview month,
        // so an instalment due later this month is eligible here exactly as it is in Review and in the Genesys upload (which sends the Review amounts). The read window `to` is already
        // clamped to the end of the current month, so nothing from the next month on can enter.
        var eligibleEnd = to;
        var eligibleRows = rows.Where(r => DateOnly.FromDateTime(r.DueDate) >= eligibleStart && DateOnly.FromDateTime(r.DueDate) <= eligibleEnd).ToList();
        var ambiguousTenants = eligibleRows.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim()))
            .Where(g => g.GroupBy(r => DateOnly.FromDateTime(r.DueDate))
                .Any(d => d.Select(r => (r.UnitId, Code: r.UnitCode.Trim())).Distinct().Count() > 1)
                || g.GroupBy(r => r.UnitId).Any(u => u.Select(r => r.UnitCode.Trim()).Distinct().Count() > 1)
                || g.GroupBy(r => r.UnitCode.Trim()).Any(u => u.Select(r => r.UnitId).Distinct().Count() > 1))
            .Select(g => g.Key).ToHashSet();
        var readGroups = rows.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim(), r.UnitId, Code: r.UnitCode.Trim())).ToDictionary(g => g.Key, g => g.ToList());
        var eligibleByUnit = eligibleRows.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim(), r.UnitId, Code: r.UnitCode.Trim())).ToDictionary(g => g.Key, g => g.ToList());
        // EVERY unit of the read scope is listed - the same units, contacts, tower names, order and Due / Overdue / Total as Receivables. The stage rule (eligibility scope) only decides READINESS:
        // a unit with no qualifying stage amount stays in the list, flagged NoQualifyingBalance / BelowThreshold (status NotEligible), and its displayed balances are never changed or dropped.
        var facts = readGroups.Select(entry =>
            {
                var key = entry.Key;
                var all = entry.Value;   // the unit's rows over the whole read scope
                var g = eligibleByUnit.TryGetValue(key, out var eligible) ? eligible : [];
                var basis = g.Count > 0 ? g : all;
                // PACT's contact of the apartment is that of its EARLIEST payment (then smallest voucher) - the representative row Receivables uses.
                var first = all.OrderBy(r => r.DueDate).ThenBy(r => r.VoucherNumber, StringComparer.Ordinal).First();
                var amount = g.Count > 0
                    ? CollectionsCampaignPolicy.Evaluate(g.Select(r => new CampaignInstalment(DateOnly.FromDateTime(r.DueDate), r.Amount)), selected, date)
                    : new CampaignAmount(0m, null, "NoQualifyingBalance");
                // Due = due inside the current month of today, Overdue = due before its first day (CollectionsDueMonth; the window never reaches the next month).
                // Nothing Due or Overdue: not listed; Minimum Total is on the unit's sum of the remaining amounts.
                var dueAmount = today is { } t1 ? all.Where(r => CollectionsDueMonth.IsDue(DateOnly.FromDateTime(r.DueDate), t1)).Sum(r => r.Amount) : 0m;
                var overdueAmount = today is { } t2 ? all.Where(r => CollectionsDueMonth.IsOverdue(DateOnly.FromDateTime(r.DueDate), t2)).Sum(r => r.Amount) : 0m;
                // Payment status: a single status keeps only its own instalments in the figures (the stage amount above is untouched).
                if (paymentFilter == CampaignPaymentFilter.Due) overdueAmount = 0m;
                else if (paymentFilter == CampaignPaymentFilter.Overdue) dueAmount = 0m;
                if (today is not null && dueAmount + overdueAmount <= 0) return null;
                if (minTotal is { } floor && dueAmount + overdueAmount <= floor) return null;
                // CRM first, PACT completing; several CRM customers or conflicting people are flagged, never merged (CollectionsContactLinker).
                var unitKey = CollectionsUnitKey.Normalize(key.Code);
                var link = unitKey is not null && crmLinks is not null && crmLinks.TryGetValue((key.CompanyId, unitKey), out var found) ? found : null;
                var linked = CollectionsContactLinker.Link(
                    new SourceContact(first.FullName.Trim(), CollectionsContactNormalizer.NormalizePhone(first.Mobile), CollectionsContactNormalizer.NormalizeEmail(first.Email)),
                    link?.Contact ?? SourceContact.Empty, link?.Status ?? CrmLinkStatus.None);
                var phone = linked.Phone;
                var email = linked.Email;
                var flags = CollectionsCampaignFlags.None;
                if (amount.Reason == "NoQualifyingBalance") flags |= CollectionsCampaignFlags.NoQualifyingBalance;
                else if (amount.Reason == "BelowThreshold") flags |= CollectionsCampaignFlags.BelowThreshold;
                else if (amount.Reason != "Qualifies") flags |= CollectionsCampaignFlags.AmbiguousInstalments;
                if (amount.Amount is { } value && value != decimal.Round(value, 2)) flags |= CollectionsCampaignFlags.AmountPrecisionNeedsReview;
                if (key.UnitId is not > 0 || string.IsNullOrWhiteSpace(key.Code) || key.Code == "0")
                    flags |= CollectionsCampaignFlags.MissingUnitIdentity;
                if (basis.Select(r => linked.CrmStatus == CrmLinkStatus.Single ? ("", "", "", r.ProjectCode) : (r.FullName.Trim(), CollectionsContactNormalizer.NormalizePhone(r.Mobile),
                        CollectionsContactNormalizer.NormalizeEmail(r.Email), r.ProjectCode)).Distinct().Count() > 1)
                    flags |= CollectionsCampaignFlags.ConflictingContactDetails;
                if (ambiguousTenants.Contains((key.CompanyId, key.Tenant))) flags |= CollectionsCampaignFlags.UnitAllocationNeedsReview;
                if (basis.Any(r => string.Equals(r.SourceStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase)))
                    flags |= CollectionsCampaignFlags.ContradictoryPaymentStatus;
                flags |= CollectionsContactLinker.Flags(linked, selected != CollectionsCampaignStage.LegalReferral);
                if (crmUnavailable) flags |= CollectionsCampaignFlags.CrmUnavailable;
                return new CampaignUnitFacts(first.CompanyId, key.Tenant, linked.NameSource == "Crm" ? linked.Name : first.FullName, phone, email, first.UnitId, key.Code, first.ProjectCode,
                    amount.Amount, amount.EarliestDueDate, (int)flags, first.TowerNumber, first.TowerName, dueAmount, overdueAmount,
                    link?.Status switch { CrmLinkStatus.Single => 1, CrmLinkStatus.Ambiguous => 2, CrmLinkStatus.CompanyAmbiguous => 3, _ => 0 }, link?.Status == CrmLinkStatus.Single ? link.CustomerId : null,
                    all.Max(r => DateOnly.FromDateTime(r.DueDate)));
            }).OfType<CampaignUnitFacts>().ToList();
        if (companyId is { } company) facts = facts.Where(f => f.CompanyId == company).ToList();
        // Floor: applied to the finished units (never to the instalments before grouping), so the flags of a unit - including the tenant-wide ones - are the same with and without the filter.
        if (floors is { Count: > 0 }) facts = facts.Where(f => CollectionsUnitFloor.Matches(f.UnitCode, floors)).ToList();
        if (term is not null)
        {
            bool Match(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
            facts = facts.Where(c => Match(c.FullName) || Match(c.TenantId) || Match(c.UnitCode) || Match(c.Phone) || Match(c.Email) || Match(c.TowerNumber ?? "") || Match(c.TowerName ?? "")
                || (phoneDigits is not null && c.Phone.Contains(phoneDigits, StringComparison.Ordinal))).ToList();
        }
        // Newest tower first (the tower NUMBER, descending: TP140, TP132 ... TP119), then the units of a tower in a fixed order; computed over the WHOLE result before it is paged
        // or exported, so every page and the files follow one order and no page repeats or skips a unit. Identical to the Receivables page (CollectionsTowerOrder).
        return facts.OrderBy(c => c, CollectionsTowerOrderComparer.Instance).ToList();
    }

    public async Task<CollectionsResult<CollectionsCampaignExportDto>> ExportAsync(CollectionsCaller caller,
        string? stage, string? mode, DateOnly? businessDate = null, int? companyId = null, string? search = null,
        CancellationToken cancellationToken = default, DateOnly? dateFrom = null, DateOnly? dateTo = null, int? towerId = null, decimal? minTotal = null, string? paymentStatus = null, IReadOnlyList<int>? towerIds = null, string? sendingMode = null, IReadOnlyList<int>? floors = null)
    {
        // No source read for an unauthorized export, including malformed requests.
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanSendReminders)
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.Forbidden);
        if (mode is not ("review" or "genesys"))
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose review or genesys export.");
        var result = await PreviewAsync(caller, stage, businessDate, companyId, search,
            forExport: true, cancellationToken: cancellationToken, dateFrom: dateFrom, dateTo: dateTo, towerId: towerId, minTotal: minTotal, paymentStatus: paymentStatus, towerIds: towerIds, sendingMode: sendingMode, floors: floors);
        if (!result.IsSuccess) return CollectionsResult<CollectionsCampaignExportDto>.Fail(result.Outcome, result.Detail);
        var report = result.Value!;
        // Freshness requirement (both export modes): the snapshot of every company in scope must be loaded and no older than
        // ReceivablesSnapshotOptions.MaxAgeMinutes. A failed latest refresh is covered by the same rule because the age keeps growing.
        // The whole requested From/To range must also be covered by the snapshot, otherwise the file would silently omit receivables.
        if (report.Snapshot is { IsReady: false } stale)
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"Receivables data is not ready to export. {stale.ReadyProblem} No file was created; once the data is loaded and fresh, retry.");
        if (mode == "genesys")
        {
            // The Genesys file carries ONLY ready units; units the stage rule does not make eligible stay in the review file with their balances but are never sent. Any unit that is eligible
            // yet not ready (needs review, preview only, internal) blocks the file: the list must be reviewed first.
            if (!report.FinancialSourceValidated)
                return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                    "The financial source is not reconciled, so no Genesys file is made from the preview. " + (report.SourceGate?.Message ?? ""));
            if (report.Stage == nameof(CollectionsCampaignStage.LegalReferral) || report.BusinessDate != report.LiveBusinessDate || !report.IsScheduledDate
                || report.Items.Any(c => c.Status is not ("Ready" or "NotEligible")) || !report.Items.Any(c => c.Status == "Ready"))
                return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                    "Genesys export needs a current scheduled date and every eligible row ready. Review the list first; internal legal referrals cannot be sent to a customer campaign.");
            report = report with { Items = report.Items.Where(c => c.Status == "Ready").ToList() };
        }
        var csv = CollectionsCampaignCsv.Write(report, review: mode == "review");
        // Review mode additionally carries the simplified employee workbook, built from the same filtered items; the audit CSV above is unchanged.
        var baseName = $"collections-{report.Stage}-{report.BusinessDate:yyyy-MM-dd}-{mode}";
        return CollectionsResult<CollectionsCampaignExportDto>.Ok(new(
            $"{baseName}.csv", csv, report.Items.Count,
            mode == "review" ? CollectionsCampaignReviewWorkbook.Write(report) : null,
            mode == "review" ? $"{baseName}.xlsx" : null));
    }
}

public static class CollectionsCampaignCsv
{
    public static string Write(CollectionsCampaignPreviewDto report, bool review)
    {
        var text = new StringBuilder();
        text.Append("RecordId,CustomerKey,CompanyId,TenantId,CustomerName,Phone,Email,UnitId,UnitCode,ProjectCode,Amount,Currency,DueDate,Stage,CycleKey,ReadAtUtc,Status,Reason,Use,VoiceEligible,SmsEligible,EmailEligible" + (review ? ",TowerNumber,TowerName" : "") + "\r\n");
        foreach (var c in report.Items)
        {
            var cells = new[] { c.RecordId, c.CustomerKey, c.CompanyId.ToString(CultureInfo.InvariantCulture), c.TenantId,
                c.CustomerName, c.Phone, c.Email, c.UnitId?.ToString(CultureInfo.InvariantCulture) ?? "", c.UnitCode,
                c.ProjectCode, c.Amount?.ToString("0.00##########################", CultureInfo.InvariantCulture) ?? "",
                c.Currency, c.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", c.Stage, c.CycleKey,
                report.ReadAtUtc.ToString("O", CultureInfo.InvariantCulture), c.Status, c.Reason,
                review ? "InternalReviewOnly" : "GenesysCampaign", !review && c.VoiceEligible ? "true" : "false",
                !review && c.SmsEligible ? "true" : "false", !review && c.EmailEligible ? "true" : "false" };
            // The internal review file also names the tower (the number and the name from the same source as the pages); the Genesys file keeps its established columns.
            if (review) cells = [.. cells, c.TowerNumber ?? "", CollectionsTowerName.IsUsable(c.TowerName, c.TowerNumber) ? c.TowerName! : ""];
            text.Append(string.Join(',', cells.Select((v, index) => Cell(v, review, isPhone: index == 5)))).Append("\r\n");
        }
        return text.ToString();
    }

    internal static string Cell(string value, bool review, bool isPhone)
    {
        // Review files are opened in spreadsheets. Machine imports preserve validated E.164 phone numbers.
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'
            && (review || !isPhone || !(trimmed.StartsWith('+') && trimmed[1..].All(char.IsAsciiDigit)))) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>The Campaigns order: <see cref="CollectionsTowerOrder"/> over the unit code, company, customer and unit id.</summary>
internal sealed class CollectionsTowerOrderComparer : IComparer<CampaignUnitFacts>
{
    public static readonly CollectionsTowerOrderComparer Instance = new();
    public int Compare(CampaignUnitFacts? a, CampaignUnitFacts? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return CollectionsTowerOrder.Compare(CollectionsTowerNumber.Parse(a.UnitCode), a.UnitCode, a.CompanyId, a.TenantId, a.UnitId ?? 0,
            CollectionsTowerNumber.Parse(b.UnitCode), b.UnitCode, b.CompanyId, b.TenantId, b.UnitId ?? 0);
    }
}
