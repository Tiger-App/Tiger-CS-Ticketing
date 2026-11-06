using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Reads the stored review data. Authorization runs first; filters, counts and pages are executed by the store in the
/// database; no financial source is called. Also defines what "selected" means for approval.
/// </summary>
public sealed class ReviewQueryService(
    CollectionsOptions options, CollectionsReviewOptions reviewOptions, CollectionsAuthorizationService authorization,
    CollectionsClock clock, IReviewStore store, ReceivablesSnapshotOptions? snapshotOptions = null, GenesysOutboundOptions? genesys = null)
{
    public const string UnpaidOrPartial = "UnpaidOrPartial";
    public const string ModeSelectedKeys = "SelectedKeys";
    public const string ModeAllMatching = "AllMatching";
    /// <summary>The customers ticked on the Campaigns page, as <see cref="UnitKey"/> values: every Ready record of those units that also matches the filter.</summary>
    public const string ModeSelectedUnits = "SelectedUnits";

    /// <summary>The identity of a Campaigns row: company, tenant and unit code.</summary>
    public static string UnitKey(int companyId, string tenantId, string unitCode) => $"{companyId}|{tenantId}|{unitCode}";

    /// <summary>The Manual amount criteria of a filter (the review default minimum applies when none was given).</summary>
    public AmountCriteria ManualCriteria(ReviewFilter filter) => ManualFilters.Criteria(filter, reviewOptions.DefaultMinimumRemaining);

    public static readonly IReadOnlyList<string> PaymentStatusChoices = [UnpaidOrPartial, "All", "Unpaid", "PartiallyPaid", "Paid", "Unknown"];

    public async Task<CollectionsResult<ReviewRunDto?>> GetCurrentRunAsync(CollectionsCaller caller, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<ReviewRunDto?>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<ReviewRunDto?>.Fail(CollectionsOutcome.Disabled);
        var active = await store.GetActiveRunAsync(ct);
        if (active is not null && TimeoutReason(active) is { } reason)
        {
            // The worker never picked the run up (or died): fail it so the page and its poll stop at "Failed" with the reason.
            await store.FailRunAsync(active.CollectionsReviewRunId, reason, clock.UtcNow, ct);
            active = await store.GetRunAsync(active.CollectionsReviewRunId, ct);
            if (active is not null) return CollectionsResult<ReviewRunDto?>.Ok(ToDto(active));
        }
        var current = await store.GetCurrentRunAsync(ct);
        var run = active ?? current;
        if (active is null)
        {
            // A refresh that failed after the published run stays visible (with its reason) until a newer one succeeds, instead of silently showing the old data.
            var latest = await store.GetLatestRunAsync(ct);
            if (latest is { Status: ReviewRunStatus.Failed } && (current is null || latest.CollectionsReviewRunId > current.CollectionsReviewRunId)) run = latest;
        }
        return CollectionsResult<ReviewRunDto?>.Ok(run is null ? null : ToDto(run));
    }

    public async Task<CollectionsResult<ReviewRunDto>> GetRunAsync(CollectionsCaller caller, long runId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.Forbidden);
        var run = await store.GetRunAsync(runId, ct);
        return run is null ? CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.NotFound) : CollectionsResult<ReviewRunDto>.Ok(ToDto(run));
    }

    public async Task<CollectionsResult<ReviewPageDto>> QueryAsync(CollectionsCaller caller, ReviewFilter? filter, int page, int pageSize, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<ReviewPageDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<ReviewPageDto>.Fail(CollectionsOutcome.Disabled);
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            return CollectionsResult<ReviewPageDto>.Fail(CollectionsOutcome.InvalidRequest, "Page must be 1 or more and page size 1-100.");
        var parsed = TryParse(filter ?? new ReviewFilter());
        if (parsed.Error is not null) return CollectionsResult<ReviewPageDto>.Fail(CollectionsOutcome.InvalidRequest, parsed.Error);

        var run = await store.GetCurrentRunAsync(ct);
        var applied = parsed.Spec!.MinRemaining ?? reviewOptions.DefaultMinimumRemaining;
        if (run is null)
            return CollectionsResult<ReviewPageDto>.Ok(new(null, new(0, 0, 0, 0, 0), page, pageSize, 0, [], applied));
        var stale = IsStale(run);
        var spec = parsed.Spec!;
        if (CollectionsSendingModes.IsManual(filter?.SendingMode))
            return spec.ReminderType is null
                ? CollectionsResult<ReviewPageDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose the campaign type to send.")
                : await QueryManualAsync(run, spec, filter!, stale, page, pageSize, applied, ct);
        var items = await store.QueryAsync(run.CollectionsReviewRunId, spec, stale, (page - 1) * pageSize, pageSize, ct);
        // Counts follow every filter except the validation-status choice itself, so the four tiles always add up.
        var counts = await store.CountAsync(run.CollectionsReviewRunId, spec with { ValidationStatus = null }, stale, ct);
        var unknown = await store.CountAsync(run.CollectionsReviewRunId,
            spec with { ValidationStatus = null, PaymentStatuses = [ReviewPaymentStatus.Unknown] }, stale, ct);
        // A tower chosen on data read before towers were stored matches nothing: say so instead of looking like "this project has no customers" (and never fall back to all projects).
        var towerMissing = (spec.TowerId is not null || spec.TowerIds is not null) && items.TotalCount == 0 && !await store.RunHasTowerDataAsync(run.CollectionsReviewRunId, ct);
        return CollectionsResult<ReviewPageDto>.Ok(new(ToDto(run), counts with { UnknownPaymentStatus = unknown.Total }, page, pageSize,
            items.TotalCount, items.Items.Select(v => ToDto(v, stale)).ToList(), applied, towerMissing));
    }

    /// <summary>Phone numbers that would be called repeatedly (several units, or several reminder types for one unit): who, how many units, and why they are held back.</summary>
    public async Task<CollectionsResult<OverlapPageDto>> OverlapsAsync(CollectionsCaller caller, int page, int pageSize, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<OverlapPageDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<OverlapPageDto>.Fail(CollectionsOutcome.Disabled);
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            return CollectionsResult<OverlapPageDto>.Fail(CollectionsOutcome.InvalidRequest, "Page must be 1 or more and page size 1-100.");
        var run = await store.GetCurrentRunAsync(ct);
        if (run is null) return CollectionsResult<OverlapPageDto>.Ok(new(0, page, pageSize, []));
        var result = await store.GetOverlapsAsync(run.CollectionsReviewRunId, (page - 1) * pageSize, pageSize, ct);
        var groups = result.Groups.Select(g => new OverlapGroupDto(g.Phone,
            g.Rows.Select(r => (r.CompanyId, r.TenantId)).Distinct().Count(),
            g.Rows.Select(r => (r.CompanyId, r.TenantId, r.UnitCode)).Distinct().Count(), g.Rows.Count,
            g.Rows.Any(r => r.Reasons.Contains($";{ReviewReasons.SharedPhoneMultipleUnits};", StringComparison.Ordinal)),
            g.Rows.Any(r => r.Reasons.Contains($";{ReviewReasons.ReminderTypeOverlap};", StringComparison.Ordinal)),
            g.Rows.Select(r => r.ReminderType).Distinct().Order().ToList(),
            g.Rows.Select(r => new OverlapRowDto(r.CustomerName, r.CompanyId, r.TenantId, r.UnitCode, r.ReminderType, r.RemainingAmount, r.Currency)).ToList())).ToList();
        return CollectionsResult<OverlapPageDto>.Ok(new(result.TotalGroups, page, pageSize, groups));
    }

    /// <summary>Resolves a selection to the exact Ready records it denotes, with the totals and contact list to confirm.</summary>
    public async Task<CollectionsResult<SelectionSummaryDto>> SummarizeAsync(CollectionsCaller caller, SelectionRequest request, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<SelectionSummaryDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<SelectionSummaryDto>.Fail(CollectionsOutcome.Disabled);
        var resolved = await ResolveAsync(request, ct);
        if (resolved.Error is not null)
            return CollectionsResult<SelectionSummaryDto>.Fail(CollectionsOutcome.InvalidRequest, resolved.Error);
        return CollectionsResult<SelectionSummaryDto>.Ok(Summarize(resolved, request));
    }

    public sealed record Resolved(CollectionsReviewRun? Run, IReadOnlyList<CollectionsReviewRecord> Records, int Skipped, string? Error);

    /// <summary>
    /// "SelectedKeys": exactly the listed records, kept only if currently Ready. "AllMatching": every Ready record that
    /// matches the filters right now, on every page, except the listed exclusions. Both end as an explicit, frozen list.
    /// </summary>
    public async Task<Resolved> ResolveAsync(SelectionRequest request, CancellationToken ct)
    {
        var run = await store.GetCurrentRunAsync(ct);
        if (run is null) return new(null, [], 0, "There is no review data yet. Run a refresh first.");
        var stale = IsStale(run);
        var max = Math.Max(1, reviewOptions.MaxSelection);
        IReadOnlyList<ReviewRecordView> views;
        int skipped = 0;
        if (string.Equals(request.Mode, ModeSelectedKeys, StringComparison.OrdinalIgnoreCase))
        {
            var keys = (request.RecordKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count == 0) return new(run, [], 0, "Select at least one record.");
            if (keys.Count > max) return new(run, [], 0, $"At most {max} records can be approved at once.");
            var all = new ReviewQuerySpec(null, null, null, null, null, null, null, null, null, null, null, null, null);
            views = await store.ResolveAsync(run.CollectionsReviewRunId, all, keys, stale, max, ct);
            skipped = keys.Count - views.Count;
        }
        else if (string.Equals(request.Mode, ModeAllMatching, StringComparison.OrdinalIgnoreCase))
        {
            var parsed = TryParse(request.Filter ?? new ReviewFilter());
            if (parsed.Error is not null) return new(run, [], 0, parsed.Error);
            if (ManualProblem(request.Filter, parsed.Spec!, run) is { } manualProblem) return new(run, [], 0, manualProblem);
            // Only Ready records can ever be approved, whatever the validation-status filter shows. (Manual mode adds the records that are Ready except for the schedule.)
            views = await ResolveReadyAsync(run, parsed.Spec!, request.Filter, null, stale, max, ct);
            if (views.Count > max) return new(run, [], 0, $"More than {max} records match. Narrow the filters (for example by company or reminder type) and approve in parts.");
            var excluded = (request.ExcludedKeys ?? []).ToHashSet(StringComparer.Ordinal);
            if (excluded.Count > 0) views = views.Where(v => !excluded.Contains(v.Record.RecordKey)).ToList();
        }
        else if (string.Equals(request.Mode, ModeSelectedUnits, StringComparison.OrdinalIgnoreCase))
        {
            var units = (request.UnitKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToHashSet(StringComparer.Ordinal);
            if (units.Count == 0) return new(run, [], 0, "Select at least one customer.");
            var parsed = TryParse(request.Filter ?? new ReviewFilter());
            if (parsed.Error is not null) return new(run, [], 0, parsed.Error);
            if (ManualProblem(request.Filter, parsed.Spec!, run) is { } manualProblem) return new(run, [], 0, manualProblem);
            views = await ResolveReadyAsync(run, parsed.Spec!, request.Filter, units, stale, max, ct);
            if (views.Count > max) return new(run, [], 0, $"At most {max} records can be approved at once.");
            // A ticked customer that has no Ready record any more (paid, already sent, changed) is counted as left out, never silently kept.
            skipped = units.Count(u => !views.Any(v => UnitKey(v.Record.CompanyId, v.Record.TenantId, v.Record.UnitCode) == u));
        }
        else return new(run, [], 0, "Choose a selection mode: SelectedKeys, SelectedUnits or AllMatching.");

        // The view must be Ready now (stale-adjusted, not already sent) and carry a confirmed amount.
        var manualMode = CollectionsSendingModes.IsManual(request.Filter?.SendingMode);
        var ready = views.Where(v => (manualMode ? ManualStatus(v, stale) : v.EffectiveStatus(stale)) == ReviewValidationStatus.Ready
            && v.Record.RemainingAmount is > 0 && v.Record.DueDate is not null)
            .Select(v => v.Record).OrderBy(r => r.RecordKey, StringComparer.Ordinal).ToList();
        skipped += views.Count - ready.Count;
        return new(run, ready, skipped, null);
    }

    /// <summary>
    /// Manual mode only: why the selection cannot be sent (null = it can, or it is Auto). Manual sending needs one campaign type (any existing one, at any time: there is no send date and no stage
    /// schedule), a contact list that suits that type, and review data built today (the repeated-call checks are evaluated for the day the data was built). Whether each CUSTOMER may be sent is
    /// decided by the stored record: reconciliation, contact, identity, shared phone, legal release, duplicates and the contact-frequency limit all stay in force.
    /// </summary>
    private string? ManualProblem(ReviewFilter? filter, ReviewQuerySpec spec, CollectionsReviewRun run)
    {
        if (!CollectionsSendingModes.IsManual(filter?.SendingMode)) return null;
        if (spec.ReminderType is not { } type) return "Choose the campaign type to send.";
        if (!CampaignReminderTypes.TryResolveManualDestination(type, filter?.ContactList, out _))
            return $"A {CampaignReminderTypes.DefaultLabel(type)} campaign cannot be sent to that contact list. Choose {string.Join(", ", CampaignReminderTypes.ManualDestinations(type).Select(CampaignReminderTypes.DefaultLabel))}.";
        var today = clock.BusinessDate;
        if (run.AsOfDate != today) return $"The review data was built for {run.AsOfDate:dd MMM yyyy}, not today ({today:dd MMM yyyy}). Refresh it, then approve.";
        return null;
    }

    private const int ManualScanLimit = 100_000;

    /// <summary>
    /// The customers of a Manual filter, each with the amount CALCULATED from its unpaid instalments and the person's Payment status, From / To and Minimum Total
    /// (<see cref="FilteredAmounts"/>), never the stage amount. A customer with nothing qualifying under those filters (zero, or not above the minimum) is not a candidate at all. The record's
    /// own status (reconciliation, contact, identity, shared phone, legal release, duplicates, frequency) is untouched. The returned records are detached copies of the stored ones carrying the
    /// calculated amount and the earliest qualifying due date: what the list shows, the approval freezes, the upload quotes and the confirmation totals.
    /// </summary>
    private async Task<List<ReviewRecordView>> ManualCalculatedAsync(CollectionsReviewRun run, ReviewQuerySpec spec, ReviewFilter? filter, IReadOnlyCollection<string>? units, bool stale, CancellationToken ct)
    {
        // The date range, Payment status and minimum are about instalments, not about the stage record's own fields: they are applied by the calculation, not by the stored-record query.
        var coarse = spec with { DueFrom = null, DueTo = null, MinRemaining = null, MaxRemaining = null, ValidationStatus = null };
        var views = await store.ResolveAsync(run.CollectionsReviewRunId, coarse, null, stale, ManualScanLimit, ct, units);
        var criteria = ManualCriteria(filter ?? new ReviewFilter());
        var today = clock.BusinessDate;
        var result = new List<ReviewRecordView>(views.Count);
        foreach (var view in views)
        {
            var calc = ManualFilters.CurrentAmount(view.Record, criteria, today);
            if (calc.Qualifies) { view.Record.RemainingAmount = calc.Amount; view.Record.DueDate = calc.EarliestDue; result.Add(view); continue; }
            if (calc.Reason is not (FilteredAmount.Ambiguous or FilteredAmount.Unavailable)) continue;   // nothing qualifies under the filters: the customer is not offered
            // Ambiguous or unreadable instalments never produce an amount. A record already held back stays visible with its reason; one that would otherwise be sendable (data written before the
            // instalments were stored) is shown as needing a refresh, never as Ready.
            view.Record.RemainingAmount = null;
            if (IsManuallySendable(view.Record))
            {
                view.Record.ValidationStatus = ReviewValidationStatus.NeedsReview;
                view.Record.Reasons = ReviewRecordBuilder.Serialize(ReviewRecordBuilder.Parse(view.Record.Reasons).Append(ReviewReasons.StaleSource));
            }
            result.Add(view);
        }
        return result;
    }

    private static bool IsManuallySendable(CollectionsReviewRecord record) => ReviewRecordBuilder.IsManuallySendable(record);

    /// <summary>The status a Manual list shows: a record held back by the stage schedule alone is Ready (the schedule is the one rule Manual waives); everything else is what it is.</summary>
    private static ReviewValidationStatus ManualStatus(ReviewRecordView v, bool stale)
    {
        var status = v.EffectiveStatus(stale);
        if (status == ReviewValidationStatus.Excluded && IsManuallySendable(v.Record)) return stale ? ReviewValidationStatus.NeedsReview : ReviewValidationStatus.Ready;
        return status;
    }

    private async Task<CollectionsResult<ReviewPageDto>> QueryManualAsync(CollectionsReviewRun run, ReviewQuerySpec spec, ReviewFilter filter, bool stale, int page, int pageSize, decimal applied, CancellationToken ct)
    {
        var calculated = await ManualCalculatedAsync(run, spec, filter, null, stale, ct);
        int Count(ReviewValidationStatus s) => calculated.Count(v => ManualStatus(v, stale) == s);
        var counts = new ReviewCountsDto(calculated.Count, Count(ReviewValidationStatus.Ready), Count(ReviewValidationStatus.NeedsReview), Count(ReviewValidationStatus.Excluded), Count(ReviewValidationStatus.AlreadySent));
        var shown = (spec.ValidationStatus is { } wanted ? calculated.Where(v => ManualStatus(v, stale) == wanted) : calculated)
            .OrderBy(v => v.Record.CompanyId).ThenBy(v => v.Record.TenantId, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Record.UnitCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Record.RecordKey, StringComparer.Ordinal).ToList();
        var readyTotal = calculated.Where(v => ManualStatus(v, stale) == ReviewValidationStatus.Ready).Sum(v => v.Record.RemainingAmount ?? 0m);
        var towerMissing = (spec.TowerId is not null || spec.TowerIds is not null) && shown.Count == 0 && !await store.RunHasTowerDataAsync(run.CollectionsReviewRunId, ct);
        return CollectionsResult<ReviewPageDto>.Ok(new(ToDto(run), counts, page, pageSize, shown.Count,
            shown.Skip((page - 1) * pageSize).Take(pageSize).Select(v => ToDto(v, stale, manual: true)).ToList(), applied, towerMissing, readyTotal));
    }

    /// <summary>Ready records of the selection. In Manual mode these are the customers the person may send now (see <see cref="ManualCalculatedAsync"/>): schedule waived, amount from the filters.</summary>
    private async Task<IReadOnlyList<ReviewRecordView>> ResolveReadyAsync(CollectionsReviewRun run, ReviewQuerySpec spec, ReviewFilter? filter, IReadOnlyCollection<string>? units, bool stale, int max, CancellationToken ct)
    {
        if (CollectionsSendingModes.IsManual(filter?.SendingMode))
            return (await ManualCalculatedAsync(run, spec, filter, units, stale, ct)).Where(v => ManualStatus(v, stale) == ReviewValidationStatus.Ready).Take(max + 1).ToList();
        return await store.ResolveAsync(run.CollectionsReviewRunId, spec with { ValidationStatus = ReviewValidationStatus.Ready }, null, stale, max, ct, units);
    }

    public static string Fingerprint(IEnumerable<CollectionsReviewRecord> records)
    {
        var text = new StringBuilder();
        foreach (var r in records.OrderBy(r => r.RecordKey, StringComparer.Ordinal))
            text.Append(r.RecordKey).Append('|').Append(r.ReminderType).Append('|')
                .Append(r.RemainingAmount?.ToString("0.00", CultureInfo.InvariantCulture)).Append('|').Append(r.Currency).Append('|')
                .Append(r.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|').Append(r.Phone).Append('|')
                .Append(r.CustomerName).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static IReadOnlyDictionary<string, decimal> TotalsByCurrency(IEnumerable<CollectionsReviewRecord> records) =>
        records.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.RemainingAmount ?? 0m));

    public static (int Records, int Numbers) SharedPhones(IEnumerable<CollectionsReviewRecord> records)
    {
        var groups = records.GroupBy(r => r.Phone).Where(g => g.Count() > 1).ToList();
        return (groups.Sum(g => g.Count()), groups.Count);
    }

    private SelectionSummaryDto Summarize(Resolved resolved, SelectionRequest request)
    {
        var records = resolved.Records;
        var shared = SharedPhones(records);
        var sharedNumbers = records.GroupBy(r => r.Phone).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var size = Math.Clamp(request.ContactPageSize, 1, 200);
        var page = Math.Max(1, request.ContactPage);
        var contacts = records.Skip((page - 1) * size).Take(size).Select(r => new SelectionContactDto(r.RecordKey,
            r.ReminderType.ToString(), r.CustomerName, r.Phone, r.Email, r.UnitCode, r.RemainingAmount ?? 0m, r.Currency,
            r.DueDate ?? default, sharedNumbers.Contains(r.Phone))).ToList();
        var notices = new List<string>();
        if (resolved.Skipped > 0) notices.Add($"{resolved.Skipped} record(s) were left out because they are not Ready now.");
        if (shared.Records > 0) notices.Add($"{shared.Numbers} phone number(s) appear on {shared.Records} records; approving all of them means repeated calls to the same number. This must be acknowledged.");
        if (records.Count > 0 && IsStale(resolved.Run!)) notices.Add("The review data is stale; refresh it before approving.");
        return new(resolved.Run!.CollectionsReviewRunId, records.Count, Fingerprint(records), TotalsByCurrency(records),
            records.GroupBy(r => r.ReminderType.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            shared.Records, shared.Numbers, page, size, contacts, notices, ContactLists(records, request.Filter), resolved.Run.SourceReconciled,
            CollectionsSendingModes.IsManual(request.Filter?.SendingMode) && records.Select(r => r.ReminderType).Distinct().ToList() is [var only]
                && CampaignReminderTypes.TryResolveManualDestination(only, request.Filter?.ContactList, out var chosen) ? chosen.ToString() : null);
    }

    /// <summary>The Genesys contact list each reminder type of the selection will be uploaded to (an unconfigured list is shown as such; confirming is then refused).</summary>
    private IReadOnlyDictionary<string, string> ContactLists(IReadOnlyList<CollectionsReviewRecord> records, ReviewFilter? filter)
    {
        var lists = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in records.Select(r => r.ReminderType).Distinct().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            try { lists[type.ToString()] = genesys is null ? "(not configured)" : CollectionsSendingModes.IsManual(filter?.SendingMode) && CampaignReminderTypes.TryResolveManualDestination(type, filter?.ContactList, out var destination) ? genesys.ContactListIdFor(destination) : genesys.ContactListIdFor(type); }
            catch (InvalidOperationException) { lists[type.ToString()] = "(not configured)"; }
        }
        return lists;
    }

    private string? TimeoutReason(CollectionsReviewRun run)
    {
        var now = clock.UtcNow;
        if (run.Status == ReviewRunStatus.Queued && now - run.RequestedAtUtc > TimeSpan.FromMinutes(Math.Max(1, reviewOptions.QueuedTimeoutMinutes)))
            return $"The refresh was not picked up by a background worker within {Math.Max(1, reviewOptions.QueuedTimeoutMinutes)} minutes. "
                 + "Check that BackgroundJobs:Enabled is true and the API's Hangfire server is running, then retry.";
        if (run.Status == ReviewRunStatus.Running && now - (run.StartedAtUtc ?? run.RequestedAtUtc) > TimeSpan.FromMinutes(Math.Max(1, reviewOptions.StuckRunMinutes)))
            return "The refresh stopped responding and was marked failed. Retry.";
        return null;
    }

    /// <summary>A review run is built from the local receivables snapshot, so it is stale when that snapshot is: the snapshot's own limit (<see cref="ReceivablesSnapshotOptions.MaxAgeMinutes"/>) applies.</summary>
    public bool IsStale(CollectionsReviewRun run) =>
        run.SourceReadAtUtc is not { } read || (snapshotOptions is null ? clock.IsStale(read) : clock.IsStale(read, snapshotOptions.MaxAgeMinutes));

    public ReviewRunDto ToDto(CollectionsReviewRun r) => new(r.CollectionsReviewRunId, r.Status.ToString(), r.Phase, r.ProgressPercent,
        r.AsOfDate, r.CompanyId, r.DueFrom, r.DueTo, r.Source, r.SourceProcedureSuffix, r.SourceReconciled, r.RequestedAtUtc,
        r.StartedAtUtc, r.CompletedAtUtc, r.SourceReadAtUtc, r.SourceRowCount, r.RecordCount, r.Error, r.IsCurrent, IsStale(r));

    /// <param name="v">The stored record with its sent / history facts.</param>
    /// <param name="stale">Whether the review run is older than the freshness window.</param>
    /// <param name="manual">A Manual list: the stage schedule is waived, so a record held back by it alone is shown as Ready and the schedule is not given as a reason.</param>
    private static ReviewRecordDto ToDto(ReviewRecordView v, bool stale, bool manual = false)
    {
        var r = v.Record;
        var reasons = ReviewRecordBuilder.Parse(r.Reasons).ToList();
        if (manual) reasons.Remove(ReviewReasons.OutsideSchedule);
        if (stale && (r.ValidationStatus == ReviewValidationStatus.Ready || (manual && IsManuallySendable(r))) && !reasons.Contains(ReviewReasons.StaleSource))
            reasons.Add(ReviewReasons.StaleSource);
        // Held back because the customer was already added to a Genesys list today by ANOTHER record (the contact-frequency limit); say so rather than "already sent".
        if (v.AlreadySent && v.PreviousDispatchStatus is null && !reasons.Contains(ReviewReasons.ContactedToday))
            reasons.Add(ReviewReasons.ContactedToday);
        return new(r.RecordKey, r.ReminderType.ToString(), r.CompanyId, r.CustomerName, r.Phone, r.Email, r.ProjectCode, r.UnitCode,
            r.PaymentStatus.ToString(), r.RemainingAmount, r.Currency, r.DueDate, (manual ? ManualStatus(v, stale) : v.EffectiveStatus(stale)).ToString(),
            reasons.Select(c => new ReviewReasonDto(c, (ReviewReasons.KindOf(c) ?? ReasonKind.NeedsReview).ToString(), ReviewReasons.Explain(c))).ToList(),
            r.Source, r.SourceReadAtUtc, v.PreviousDispatchStatus, v.PreviousDispatchAtUtc, r.TowerNumber, r.TowerName, r.TenantId);
    }

    public (ReviewQuerySpec? Spec, string? Error) TryParse(ReviewFilter f)
    {
        if (f.CompanyId is not (null or 4 or 32)) return (null, "Company must be 4 or 32.");
        if ((f.Year is null) != (f.Month is null) || f.Year is < 2000 or > 2100 || f.Month is < 1 or > 12)
            return (null, "Year and month go together (2000-2100, 1-12).");
        if (f.DueFrom is { } from && f.DueTo is { } to && from > to) return (null, "The due-date range starts after it ends.");
        if (f.TowerId is <= 0 || f.TowerIds?.Any(t => t <= 0) == true) return (null, "Choose a valid project / tower.");
        if (f.MinRemaining is < 0 || f.MaxRemaining is < 0 || (f.MinRemaining is { } lo && f.MaxRemaining is { } hi && lo > hi))
            return (null, "Minimum remaining must not exceed maximum remaining, and neither may be negative.");
        if (new[] { f.Project, f.Unit, f.Customer, f.Reason }.Any(v => v?.Length > 200)) return (null, "Text filters are limited to 200 characters.");

        DateOnly? dueFrom = f.DueFrom, dueTo = f.DueTo;
        if (f.Year is { } y && f.Month is { } m)
        {
            var first = new DateOnly(y, m, 1);
            var last = new DateOnly(y, m, DateTime.DaysInMonth(y, m));
            dueFrom = dueFrom is { } a && a > first ? a : first;
            dueTo = dueTo is { } b && b < last ? b : last;
            if (dueFrom > dueTo) return (null, "The custom range does not overlap the selected month.");
        }

        // Due / Overdue / Both: Due = due inside the current Dubai month, Overdue = due before it (the Campaigns rule, CollectionsDueMonth). It narrows the due-date range only,
        // so it travels inside the filter that approval and dispatch re-resolve, and it never touches validation or the stage rules.
        if (!CampaignPaymentFilters.TryParse(f.DueStatus, out var dueStatus)) return (null, "Due status must be Due, Overdue or Both.");
        if (dueStatus != CampaignPaymentFilter.Both)
        {
            var monthStart = new DateOnly(clock.BusinessDate.Year, clock.BusinessDate.Month, 1);
            if (dueStatus == CampaignPaymentFilter.Due) dueFrom = dueFrom is { } d1 && d1 > monthStart ? d1 : monthStart;
            else dueTo = dueTo is { } d2 && d2 < monthStart.AddDays(-1) ? d2 : monthStart.AddDays(-1);
        }

        IReadOnlyList<ReviewPaymentStatus>? statuses;
        var payment = string.IsNullOrWhiteSpace(f.PaymentStatus) ? UnpaidOrPartial : f.PaymentStatus.Trim();
        if (payment.Equals("All", StringComparison.OrdinalIgnoreCase)) statuses = null;
        else if (payment.Equals(UnpaidOrPartial, StringComparison.OrdinalIgnoreCase)) statuses = [ReviewPaymentStatus.Unpaid, ReviewPaymentStatus.PartiallyPaid];
        else if (CollectionsEnums.TryParse<ReviewPaymentStatus>(payment, out var one)) statuses = [one];
        else return (null, "Payment status must be All, Unpaid, PartiallyPaid, Paid, Unknown or UnpaidOrPartial.");

        CampaignReminderType? type = null;
        if (!string.IsNullOrWhiteSpace(f.ReminderType))
        {
            if (!CollectionsEnums.TryParse<CampaignReminderType>(f.ReminderType.Replace(" ", ""), out var t))
                return (null, "Reminder type must be CurrentMonth, FollowUp, Overdue, LegalNotice or LegalCase.");
            type = t;
        }
        ReviewValidationStatus? validation = null;
        if (!string.IsNullOrWhiteSpace(f.ValidationStatus) && !f.ValidationStatus.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            if (!CollectionsEnums.TryParse<ReviewValidationStatus>(f.ValidationStatus.Replace(" ", ""), out var v))
                return (null, "Validation status must be Ready, NeedsReview, Excluded or AlreadySent.");
            validation = v;
        }
        string? reason = null;
        if (!string.IsNullOrWhiteSpace(f.Reason))
        {
            reason = f.Reason.Trim();
            if (ReviewReasons.KindOf(reason) is null) return (null, "Unknown validation reason.");
        }

        if (!CollectionsSendingModes.TryParse(f.SendingMode, out _)) return (null, "Sending mode must be Manual or Auto.");
        var customer = f.Customer?.Trim();
        string? digits = null;
        if (!string.IsNullOrEmpty(customer) && !customer.Any(char.IsLetter))
        {
            var d = new string(customer.Where(char.IsAsciiDigit).ToArray());
            if (d.Length >= 3) digits = d.TrimStart('0');
        }
        var towerSet = TowerSet(f);
        return (new ReviewQuerySpec(f.CompanyId, Blank(f.Project), Blank(f.Unit), Blank(customer), digits, dueFrom, dueTo, statuses,
            f.MinRemaining ?? reviewOptions.DefaultMinimumRemaining, f.MaxRemaining, type, validation, reason, towerSet is { Count: 1 } onlyTower ? onlyTower[0] : null, towerSet is { Count: > 1 } manyTowers ? manyTowers : null,
            // A Manual or Auto dispatch request carries the contact-frequency limit: a customer already added to a Genesys dispatch today is held back.
            string.IsNullOrWhiteSpace(f.SendingMode) ? null : clock.BusinessDayStartUtc), null);
    }

    /// <summary>The distinct tower ids of a filter (the single id and the list together); empty = All projects.</summary>
    private static List<int> TowerSet(ReviewFilter f) => (f.TowerIds ?? []).Concat(f.TowerId is { } one ? new[] { one } : Array.Empty<int>()).Distinct().Order().ToList();

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
