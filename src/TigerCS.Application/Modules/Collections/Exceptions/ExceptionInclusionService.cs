using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Exceptions;

/// <summary>Which GULF_CUSTOMERS units to list. <c>Search</c> is one box over unit code, tower and customer (the fields the Campaigns search uses).</summary>
public sealed record ExceptionListRequest(string? ReminderType, string? Search = null, IReadOnlyList<int>? TowerIds = null, int Page = 1, int PageSize = 25);

/// <summary>Units to include or remove, as listed by <see cref="ExceptionListDto"/> (<c>company|tenant|unitCode</c>). They are re-checked against the stored data; nothing the client sends is trusted.</summary>
public sealed record ExceptionChangeRequest(string? ReminderType, IReadOnlyList<string>? UnitKeys);

public sealed record ExceptionUnitDto(
    string UnitKey, int CompanyId, string TenantId, string UnitCode, string CustomerName, string? TowerNumber, string? TowerName, decimal? Amount, string Currency,
    bool ManuallyIncluded, DateTime? IncludedAtUtc, bool IncludedByMe, bool SpecialCase, bool EligibleOtherwise, IReadOnlyList<ReviewReasonDto> OtherReasons);

public sealed record ExceptionListDto(
    string CampaignKey, string ReminderType, DateOnly BusinessDate, bool InclusionDay, int TotalCount, int IncludedCount, int Page, int PageSize, IReadOnlyList<ExceptionUnitDto> Items);

public sealed record ExceptionRejectedDto(string UnitKey, string Reason);

public sealed record ExceptionChangeResultDto(string CampaignKey, int Changed, int Unchanged, IReadOnlyList<ExceptionRejectedDto> Rejected);

/// <summary>
/// "Manage Exceptions": the GULF_CUSTOMERS units of the current approval data, and the authorized person's manual inclusion of selected ones in the current Manual campaign.
/// <list type="bullet">
/// <item>Needs the send right (the same as Manual Preview export and Send). Authorization first; no data is read for anyone else.</item>
/// <item>An inclusion belongs to ONE campaign (Manual, today's Dubai date, the campaign type). It is never permanent, never touches Receivables, never applies to a unit that was not selected, and never overrides a permanent special case.</item>
/// <item>Only a unit that is a GULF_CUSTOMERS unit of the current approval data for that campaign type can be included; the client's unit key is a look-up key, not a fact.</item>
/// <item>Each change is audited (who, when, campaign, unit). The inclusion is read again by Preview, Export, Approval and, last, right before the Genesys upload.</item>
/// </list>
/// </summary>
public sealed class ExceptionInclusionService(
    CollectionsOptions options, CollectionsAuthorizationService authorization, CollectionsClock clock, CollectionsExceptionPolicy policy, IReviewStore store,
    IExceptionInclusionStore inclusions, IAuditEntryWriter audit, ICollectionsUnitOfWork unitOfWork, CollectionsReviewOptions reviewOptions, ILogger<ExceptionInclusionService> logger)
{
    public const string AddedAction = "CollectionsGulfInclusionAdded", RemovedAction = "CollectionsGulfInclusionRemoved", AuditEntityType = "CollectionsExceptionInclusion";
    public const int MaxUnitsPerRequest = 500;

    public async Task<CollectionsResult<ExceptionListDto>> ListAsync(CollectionsCaller caller, ExceptionListRequest request, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders) return CollectionsResult<ExceptionListDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<ExceptionListDto>.Fail(CollectionsOutcome.Disabled);
        if (!TryType(request.ReminderType, out var type)) return CollectionsResult<ExceptionListDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose the campaign type.");
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || (request.Search?.Length ?? 0) > 200 || request.TowerIds?.Any(t => t <= 0) == true)
            return CollectionsResult<ExceptionListDto>.Fail(CollectionsOutcome.InvalidRequest, "Page must be 1 or more, page size 1-100, search up to 200 characters.");
        var today = clock.BusinessDate;
        var key = CollectionsExceptionPolicy.CampaignKey(today, type);
        var run = await store.GetCurrentRunAsync(ct);
        if (run is null || policy.GulfUnitCodes.Count == 0)
            return CollectionsResult<ExceptionListDto>.Ok(new(key, type.ToString(), today, policy.IsInclusionDay(today), 0, 0, request.Page, request.PageSize, []));

        var active = (await inclusions.ListActiveAsync(key, ct)).ToDictionary(i => Identity(i.CompanyId, i.TenantId, i.UnitCode), StringComparer.Ordinal);
        var search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim();
        string? digits = null;
        if (search is not null && !search.Any(char.IsLetter)) { var d = new string(search.Where(char.IsAsciiDigit).ToArray()); if (d.Length >= 3) digits = d.TrimStart('0'); }
        var spec = new ReviewQuerySpec(null, null, null, search, digits, null, null, null, null, null, type, null, null,
            TowerIds: request.TowerIds is { Count: > 0 } towers ? towers : null, UnitCodes: policy.GulfUnitCodes);
        var stale = run.SourceReadAtUtc is not { } read || clock.IsStale(read);
        // The matching customers are read in full (the GULF_CUSTOMERS list is small) so the page, the total and the included count agree; a unit code is matched exactly after normalization.
        var page = await store.QueryAsync(run.CollectionsReviewRunId, spec, stale, 0, Math.Max(1, reviewOptions.MaxSelection), ct);
        var units = page.Items.Where(v => policy.IsGulfCustomer(v.Record.UnitCode))
            .OrderBy(v => v.Record.CompanyId).ThenBy(v => v.Record.TenantId, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Record.UnitCode, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = units.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).Select(v => ToDto(v.Record, active, caller.EmployeeId)).ToList();
        return CollectionsResult<ExceptionListDto>.Ok(new(key, type.ToString(), today, policy.IsInclusionDay(today), units.Count,
            units.Count(v => active.ContainsKey(Identity(v.Record.CompanyId, v.Record.TenantId, v.Record.UnitCode))), request.Page, request.PageSize, shown));
    }

    public Task<CollectionsResult<ExceptionChangeResultDto>> IncludeAsync(CollectionsCaller caller, ExceptionChangeRequest request, CancellationToken ct) => ChangeAsync(caller, request, include: true, ct);

    public Task<CollectionsResult<ExceptionChangeResultDto>> RemoveAsync(CollectionsCaller caller, ExceptionChangeRequest request, CancellationToken ct) => ChangeAsync(caller, request, include: false, ct);

    private async Task<CollectionsResult<ExceptionChangeResultDto>> ChangeAsync(CollectionsCaller caller, ExceptionChangeRequest request, bool include, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders) return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.Disabled);
        if (!TryType(request.ReminderType, out var type)) return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose the campaign type.");
        var wanted = (request.UnitKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.InvalidRequest, "Select at least one unit.");
        if (wanted.Count > MaxUnitsPerRequest) return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.InvalidRequest, $"At most {MaxUnitsPerRequest} units can be changed at once.");
        var today = clock.BusinessDate;
        var key = CollectionsExceptionPolicy.CampaignKey(today, type);
        var rejected = new List<ExceptionRejectedDto>();
        int changed = 0, unchanged = 0;
        var now = clock.UtcNow;

        // The units come from the stored approval data of this campaign type, never from the request: a key that is not a GULF_CUSTOMERS unit of the data is rejected.
        var known = new Dictionary<string, CollectionsReviewRecord>(StringComparer.Ordinal);
        if (include)
        {
            var run = await store.GetCurrentRunAsync(ct);
            if (run is null) return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.ReviewRequired, "There is no approval data yet. Refresh it first.");
            if (run.AsOfDate != today) return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.ReviewRequired, "The approval data was built for another day. Refresh it first.");
            var codes = wanted.Select(UnitCodeOf).Where(c => c.Length > 0 && policy.IsGulfCustomer(c)).Distinct(StringComparer.Ordinal).ToList();
            var spec = new ReviewQuerySpec(null, null, null, null, null, null, null, null, null, null, type, null, null, UnitCodes: codes);
            var page = await store.QueryAsync(run.CollectionsReviewRunId, spec, false, 0, Math.Max(1, reviewOptions.MaxSelection), ct);
            foreach (var v in page.Items) known[ReviewQueryService.UnitKey(v.Record.CompanyId, v.Record.TenantId, v.Record.UnitCode)] = v.Record;
        }

        foreach (var unitKey in wanted)
        {
            var parts = unitKey.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var company) || parts[1].Length == 0 || parts[2].Length == 0)
            { rejected.Add(new(unitKey, "Not a valid unit.")); continue; }
            var tenant = parts[1].Trim();
            var code = UnitCodeNormalizer.Normalize(parts[2]);
            if (include)
            {
                if (policy.IsSpecialCase(code)) { rejected.Add(new(unitKey, "A permanent special case can never be included.")); Log("Rejected", code, ReviewReasons.SpecialCaseExcluded); continue; }
                if (!policy.IsGulfCustomer(code)) { rejected.Add(new(unitKey, "Not a GULF_CUSTOMERS unit.")); continue; }
                if (!known.ContainsKey(ReviewQueryService.UnitKey(company, tenant, parts[2].Trim())) && !known.Values.Any(r => r.CompanyId == company && r.TenantId == tenant && UnitCodeNormalizer.Normalize(r.UnitCode) == code))
                { rejected.Add(new(unitKey, "This unit is not in the current approval data for this campaign type.")); continue; }
                var row = new CollectionsExceptionInclusion { CampaignKey = key, CompanyId = company, TenantId = tenant, UnitCode = code, IncludedByEmployeeId = caller.EmployeeId, IncludedAtUtc = now };
                if (await inclusions.StageAddAsync(row, ct))
                {
                    await audit.WriteAsync(caller.EmployeeId, AddedAction, AuditEntityType, $"{key}|{company}|{code}", null, AuditDetails(key, company, code), Guid.NewGuid(), ct);
                    changed++;
                }
                else unchanged++;
            }
            else if (await inclusions.StageRemoveAsync(key, company, tenant, code, caller.EmployeeId, now, ct))
            {
                await audit.WriteAsync(caller.EmployeeId, RemovedAction, AuditEntityType, $"{key}|{company}|{code}", AuditDetails(key, company, code), null, Guid.NewGuid(), ct);
                changed++;
            }
            else unchanged++;
        }

        if (changed > 0)
        {
            try { await unitOfWork.SaveChangesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unitOfWork.DiscardPendingChanges();
                logger.LogError("Manual GULF_CUSTOMERS inclusion could not be saved ({ExceptionType}); nothing was changed.", ex.GetType().Name);
                return CollectionsResult<ExceptionChangeResultDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The change could not be saved. Nothing was changed; try again.");
            }
            logger.LogInformation("Manual GULF_CUSTOMERS inclusion {Action} for campaign {Campaign}: {Changed} unit(s) by {Employee}.", include ? "added" : "removed", key, changed, caller.EmployeeId);
        }
        return CollectionsResult<ExceptionChangeResultDto>.Ok(new(key, changed, unchanged, rejected));

        void Log(string outcome, string code, string reason) => logger.LogInformation("GULF_CUSTOMERS inclusion {Outcome} for unit {UnitCode}: {Reason}.", outcome, code, reason);
    }

    /// <summary>The active inclusions of a campaign as a lookup the policy callers use: <c>company|tenant|normalized unit code</c>.</summary>
    public static HashSet<string> IncludedSet(IEnumerable<CollectionsExceptionInclusion> active) =>
        active.Select(i => Identity(i.CompanyId, i.TenantId, i.UnitCode)).ToHashSet(StringComparer.Ordinal);

    public static string Identity(int companyId, string tenantId, string unitCode) => $"{companyId}|{tenantId.Trim()}|{UnitCodeNormalizer.Normalize(unitCode)}";

    private static string UnitCodeOf(string unitKey) { var p = unitKey.Split('|'); return p.Length == 3 ? UnitCodeNormalizer.Normalize(p[2]) : ""; }

    private static bool TryType(string? text, out CampaignReminderType type)
    {
        type = default;
        return !string.IsNullOrWhiteSpace(text) && CollectionsEnums.TryParse(text.Replace(" ", ""), out type);
    }

    private ExceptionUnitDto ToDto(CollectionsReviewRecord r, Dictionary<string, CollectionsExceptionInclusion> active, Guid caller)
    {
        var included = active.TryGetValue(Identity(r.CompanyId, r.TenantId, r.UnitCode), out var inclusion);
        var other = ReviewRecordBuilder.Parse(r.Reasons).Where(c => c is not (ReviewReasons.GulfCustomerExcluded or ReviewReasons.OutsideSchedule or ReviewReasons.SpecialCaseExcluded)).ToList();
        var eligible = !other.Any(c => ReviewReasons.KindOf(c) is ReasonKind.NeedsReview or ReasonKind.Exclusion or ReasonKind.ManualOnly);
        return new(ReviewQueryService.UnitKey(r.CompanyId, r.TenantId, r.UnitCode), r.CompanyId, r.TenantId, r.UnitCode, r.CustomerName, r.TowerNumber, r.TowerName, r.RemainingAmount, r.Currency,
            included, inclusion?.IncludedAtUtc, included && inclusion!.IncludedByEmployeeId == caller, policy.IsSpecialCase(r.UnitCode), eligible,
            other.Select(c => new ReviewReasonDto(c, (ReviewReasons.KindOf(c) ?? ReasonKind.NeedsReview).ToString(), ReviewReasons.Explain(c))).ToList());
    }

    /// <summary>What the audit entry holds: the campaign and the unit. No customer name, phone or e-mail.</summary>
    private static string AuditDetails(string campaignKey, int companyId, string unitCode) => JsonSerializer.Serialize(new { campaign = campaignKey, companyId, unitCode });
}
