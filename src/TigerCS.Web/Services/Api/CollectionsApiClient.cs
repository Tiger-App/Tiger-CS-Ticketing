using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// The Payment tab's only route to Collections data: TigerCS.Api's internal
/// <c>/api/collections</c> routes, called server-side as the signed-in user
/// (BearerTokenHandler). The browser never sees an Api credential, a Genesys
/// credential or the financial source — and nothing here touches SQL or EF.
/// </summary>
public sealed class CollectionsApiClient(HttpClient httpClient, ILogger<CollectionsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    private const string Base = "api/collections";
    public const int PageSize = 50;

    public Task<ApiResult<CollectionsCampaignPreviewDto>> GetCampaignPreviewAsync(string stage, DateOnly? businessDate,
        int? companyId, string? search, int page, CancellationToken cancellationToken,
        DateOnly? dateFrom = null, DateOnly? dateTo = null, int? towerId = null, decimal? minTotal = null, string? paymentStatus = null, IReadOnlyList<int>? towerIds = null) =>
        GetAsync<CollectionsCampaignPreviewDto>($"{Base}/campaigns/preview?{CampaignQuery(stage, businessDate, companyId, search, dateFrom, dateTo, towerId, minTotal, paymentStatus, towerIds)}&page={Id(page)}", cancellationToken);

    public Task<ApiResult<CollectionsCampaignExportDto>> GetCampaignExportAsync(string stage, string mode,
        DateOnly? businessDate, int? companyId, string? search, CancellationToken cancellationToken,
        DateOnly? dateFrom = null, DateOnly? dateTo = null, int? towerId = null, decimal? minTotal = null, string? paymentStatus = null, IReadOnlyList<int>? towerIds = null) =>
        GetAsync<CollectionsCampaignExportDto>($"{Base}/campaigns/export?{CampaignQuery(stage, businessDate, companyId, search, dateFrom, dateTo, towerId, minTotal, paymentStatus, towerIds)}&mode={Uri.EscapeDataString(mode)}", cancellationToken);

    /// <summary>Instalment-level list from the local snapshot (the Receivables page).</summary>
    public Task<ApiResult<PactInstalmentsPageDto>> GetInstalmentsAsync(int? towerId, DateOnly? dateFrom, DateOnly? dateTo, string? paymentStatus, decimal? minAmount,
        string? search, int page, CancellationToken cancellationToken, string? view = null, string? dueMonth = null, string? status = null, decimal? minTotal = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(view)) query["view"] = view;
        if (!string.IsNullOrWhiteSpace(status)) query["status"] = status;
        if (!string.IsNullOrWhiteSpace(dueMonth)) query["dueMonth"] = dueMonth;
        if (towerId is { } tower) query["towerId"] = Id(tower);
        foreach (var id in towerIds ?? []) query.Add("towerIds", Id(id));
        if (dateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (dateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(paymentStatus)) query["paymentStatus"] = paymentStatus;
        if (minAmount is { } minimum) query["minAmount"] = minimum.ToString("0.####", CultureInfo.InvariantCulture);
        if (minTotal is { } total) query["minTotal"] = total.ToString("0.####", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        query["page"] = Id(page);
        query["pageSize"] = "25";
        return GetAsync<PactInstalmentsPageDto>($"{Base}/receivables/instalments?{query}", cancellationToken);
    }

    /// <summary>Asks the API to load a due-date range the snapshot does not cover (background; returns immediately).</summary>
    public Task<ApiResult<ReceivablesRangeLoadDto>> RequestCoverageLoadAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken, int? companyId = null) =>
        PostAsync<object, ReceivablesRangeLoadDto>(
            $"{Base}/receivables/coverage/load?dateFrom={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&dateTo={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + (companyId is { } company ? $"&companyId={Id(company)}" : ""),
            new { }, cancellationToken);

    /// <summary>Per-company refresh state only (small); polled while a refresh runs.</summary>
    public Task<ApiResult<SnapshotStatusDto>> GetSnapshotStatusAsync(CancellationToken cancellationToken) =>
        GetAsync<SnapshotStatusDto>($"{Base}/receivables/snapshot/status", cancellationToken);

    /// <summary>Active towers for the searchable tower dropdown (local table; no PACT call).</summary>
    public Task<ApiResult<List<CollectionsTowerDto>>> GetTowersAsync(CancellationToken cancellationToken) =>
        GetAsync<List<CollectionsTowerDto>>($"{Base}/receivables/towers", cancellationToken);

    // ---- reconciliation of the snapshot with PACT (explicit, never triggered by a page view)

    public Task<ApiResult<ReconciliationOverviewDto>> GetReconciliationAsync(CancellationToken cancellationToken) =>
        GetAsync<ReconciliationOverviewDto>("api/collections/reconciliation", cancellationToken);

    public Task<ApiResult<ReconciliationRunDto>> StartReconciliationAsync(int? companyId, CancellationToken cancellationToken) =>
        PostAsync<object, ReconciliationRunDto>("api/collections/reconciliation/run", new { companyId }, cancellationToken);

    // ---- review and approval (stored review data; the page never reads the financial source)

    private const string ReviewBase = "api/collections/review";

    public Task<ApiResult<ReviewRunDto?>> GetReviewRunAsync(CancellationToken cancellationToken) =>
        GetAsync<ReviewRunDto?>($"{ReviewBase}/runs/current", cancellationToken);

    public Task<ApiResult<ReviewRunDto>> StartReviewRefreshAsync(int? companyId, CancellationToken cancellationToken) =>
        PostAsync<object, ReviewRunDto>($"{ReviewBase}/refresh", new { companyId }, cancellationToken);

    public Task<ApiResult<ReviewPageDto>> GetReviewPageAsync(ReviewFilter filter, int page, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) query[name] = value; }
        Add("companyId", filter.CompanyId?.ToString(CultureInfo.InvariantCulture));
        Add("project", filter.Project); Add("towerId", filter.TowerId?.ToString(CultureInfo.InvariantCulture)); foreach (var id in filter.TowerIds ?? []) query.Add("towerIds", id.ToString(CultureInfo.InvariantCulture));
        Add("unit", filter.Unit); Add("customer", filter.Customer);
        Add("year", filter.Year?.ToString(CultureInfo.InvariantCulture)); Add("month", filter.Month?.ToString(CultureInfo.InvariantCulture));
        Add("dueFrom", filter.DueFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("dueTo", filter.DueTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("paymentStatus", filter.PaymentStatus); Add("dueStatus", filter.DueStatus);
        Add("minRemaining", filter.MinRemaining?.ToString(CultureInfo.InvariantCulture));
        Add("maxRemaining", filter.MaxRemaining?.ToString(CultureInfo.InvariantCulture));
        Add("reminderType", filter.ReminderType); Add("validationStatus", filter.ValidationStatus); Add("reason", filter.Reason);
        query["page"] = Id(page);
        query["pageSize"] = "25";
        return GetAsync<ReviewPageDto>($"{ReviewBase}/records?{query}", cancellationToken);
    }

    public Task<ApiResult<SelectionSummaryDto>> SummarizeSelectionAsync(SelectionRequest request, CancellationToken cancellationToken) =>
        PostAsync<SelectionRequest, SelectionSummaryDto>($"{ReviewBase}/selection/summary", request, cancellationToken);

    public Task<ApiResult<DispatchDto>> ConfirmDispatchAsync(ConfirmDispatchRequest request, CancellationToken cancellationToken) =>
        PostAsync<ConfirmDispatchRequest, DispatchDto>($"{ReviewBase}/dispatches", request,
            new Dictionary<string, string> { ["Idempotency-Key"] = request.IdempotencyKey }, cancellationToken);

    public Task<ApiResult<OverlapPageDto>> GetOverlapsAsync(int page, CancellationToken cancellationToken) =>
        GetAsync<OverlapPageDto>($"{ReviewBase}/overlaps?page={Id(page)}&pageSize=25", cancellationToken);

    /// <summary>The CSV of the contacts of one send, exactly as uploaded; null when the Api refuses or is unreachable.</summary>
    public async Task<byte[]?> GetDispatchContactsCsvAsync(Guid dispatchId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync($"{ReviewBase}/dispatches/{dispatchId:D}/contacts", cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : null;
        }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>The latest approved sends (newest first) with their batch results: the Send history.</summary>
    public Task<ApiResult<IReadOnlyList<DispatchDto>>> ListDispatchesAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<DispatchDto>>($"{ReviewBase}/dispatches", cancellationToken);

    public Task<ApiResult<DispatchDto>> GetDispatchAsync(Guid dispatchId, CancellationToken cancellationToken) =>
        GetAsync<DispatchDto>($"{ReviewBase}/dispatches/{dispatchId:D}", cancellationToken);

    public Task<ApiResult<DispatchDto>> RetryAdminEmailAsync(Guid dispatchId, CancellationToken cancellationToken) =>
        PostAsync<object, DispatchDto>($"{ReviewBase}/dispatches/{dispatchId:D}/admin-email/retry", new { }, cancellationToken);

    public Task<ApiResult<DispatchDto>> ReconcileBatchAsync(Guid dispatchId, long batchId, ReconcileBatchRequest request, CancellationToken cancellationToken) =>
        PostAsync<ReconcileBatchRequest, DispatchDto>($"{ReviewBase}/dispatches/{dispatchId:D}/batches/{batchId.ToString(CultureInfo.InvariantCulture)}/reconcile", request, cancellationToken);

    private static string CampaignQuery(string stage, DateOnly? businessDate, int? companyId, string? search,
        DateOnly? dateFrom = null, DateOnly? dateTo = null, int? towerId = null, decimal? minTotal = null, string? paymentStatus = null, IReadOnlyList<int>? towerIds = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["stage"] = stage;
        if (!string.IsNullOrWhiteSpace(paymentStatus)) query["paymentStatus"] = paymentStatus;
        if (minTotal is { } minimum) query["minTotal"] = minimum.ToString("0.####", CultureInfo.InvariantCulture);
        if (towerId is { } tower) query["towerId"] = Id(tower);
        if (dateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (dateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (businessDate is { } date) query["businessDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (companyId is { } company) query["companyId"] = Id(company);
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        return query.ToString()!;
    }

    public Task<ApiResult<PactReceivableCustomersDto>> GetReceivableCustomersAsync(
        int? companyId, string? status, string? search, int page, CancellationToken cancellationToken, int? year = null, int? month = null,
        int? towerId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (towerId is { } tower) query["towerId"] = Id(tower);
        if (dateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (dateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (year is { } y) query["year"] = Id(y);
        if (month is { } m) query["month"] = Id(m);
        if (companyId is { } company) query["companyId"] = Id(company);
        if (!string.IsNullOrWhiteSpace(status)) query["status"] = status;
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        query["page"] = Id(page);
        query["pageSize"] = "25";
        return GetAsync<PactReceivableCustomersDto>($"{Base}/receivables/customers?{query}", cancellationToken);
    }

    /// <summary>EDSM's payment summary, resolved server-side from the customer key (PACT customers only).</summary>
    public Task<ApiResult<CollectionsPaymentSummaryResponseDto>> GetPaymentSummaryAsync(string customerKey, CancellationToken cancellationToken) =>
        GetAsync<CollectionsPaymentSummaryResponseDto>($"{Base}/customers/by-key/{Uri.EscapeDataString(customerKey)}/payment-summary", cancellationToken);

    public Task<ApiResult<CollectionsPaymentSummaryResponseDto>> GetLookupPaymentSummaryAsync(
        string phoneNumber, string customerKey, CancellationToken cancellationToken) =>
        GetAsync<CollectionsPaymentSummaryResponseDto>($"{Base}/customer-lookup/payment-summary?phoneNumber={Uri.EscapeDataString(phoneNumber)}&customerKey={Uri.EscapeDataString(customerKey)}", cancellationToken);

    /// <summary>The unit-based financial lookup of a CRM customer (CRM units -> PACT by company + tower + apartment); the phone is optional and must resolve to the same CRM customer.</summary>
    public Task<ApiResult<CustomerUnitLinkResultDto>> GetCrmCustomerUnitsAsync(long crmCustomerId, string? phone, string? selection, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(phone)) query["phone"] = phone;
        if (!string.IsNullOrWhiteSpace(selection)) query["selection"] = selection;
        return GetAsync<CustomerUnitLinkResultDto>($"{Base}/customers/crm/{Id(crmCustomerId)}/units?{query}", cancellationToken);
    }

    /// <summary>Customer and unit linking by a typed phone number: CRM first, PACT (incl. Leasing) when CRM holds no eligible customer.</summary>
    public Task<ApiResult<CustomerUnitLinkResultDto>> LookupCustomerUnitsAsync(string phone, string? selection, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["phone"] = phone;
        if (!string.IsNullOrWhiteSpace(selection)) query["selection"] = selection;
        return GetAsync<CustomerUnitLinkResultDto>($"{Base}/customers/lookup?{query}", cancellationToken);
    }

    public Task<ApiResult<CollectionsOutstandingResponseDto>> GetOutstandingAsync(long crmCustomerId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsOutstandingResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/outstanding?pageSize=100", cancellationToken);

    public Task<ApiResult<CollectionsInstalmentsResponseDto>> GetInstalmentsAsync(long crmCustomerId, string accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsInstalmentsResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/payments?{Query(accountId, ("view", "instalments"))}", cancellationToken);

    public Task<ApiResult<CollectionsPaymentHistoryResponseDto>> GetPaymentHistoryAsync(long crmCustomerId, string accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsPaymentHistoryResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/payments?{Query(accountId, ("view", "history"))}", cancellationToken);

    public Task<ApiResult<CollectionsReminderHistoryResponseDto>> GetRemindersAsync(long crmCustomerId, string? accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsReminderHistoryResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/reminders?{Query(accountId)}", cancellationToken);

    public Task<ApiResult<CollectionsReminderCandidatesResponseDto>> GetCandidatesAsync(
        string reminderType, long crmCustomerId, string accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsReminderCandidatesResponseDto>(
            $"{Base}/reminders/candidates?{Query(accountId, ("reminderType", reminderType), ("crmCustomerId", Id(crmCustomerId)))}", cancellationToken);

    public Task<ApiResult<CollectionsReminderJobDto>> QueueReminderAsync(
        QueueCollectionsReminderRequestDto request, string idempotencyKey, CancellationToken cancellationToken) =>
        PostAsync<QueueCollectionsReminderRequestDto, CollectionsReminderJobDto>(
            $"{Base}/reminders", request, new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey }, cancellationToken);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Query(string? accountId, params (string Name, string Value)[] extra)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(accountId)) query["accountId"] = accountId;
        foreach (var (name, value) in extra) query[name] = value;
        query["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture);
        return query.ToString()!;
    }
}
