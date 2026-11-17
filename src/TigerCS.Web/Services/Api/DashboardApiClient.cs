using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/dashboard</c> endpoints — the operational KPI
/// counts, breakdowns and Recent/Critical rows, computed server-side over
/// the caller's own visible-department scope.
/// </summary>
public sealed class DashboardApiClient(HttpClient httpClient, ILogger<DashboardApiClient> logger)
    : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<DashboardSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken) =>
        GetAsync<DashboardSummaryDto>("api/dashboard", cancellationToken);

    /// <summary>The Operational Dashboard (Dashboard Phase 1) under the given filters; the Api applies the caller's scope and echoes the filters as actually applied.</summary>
    public Task<ApiResult<DashboardOverviewDto>> GetOverviewAsync(DashboardOverviewRequestDto request, CancellationToken cancellationToken) =>
        GetAsync<DashboardOverviewDto>(WithFilters("api/dashboard/overview", request), cancellationToken);

    /// <summary>The Customers by Location card under the same filters — a read of its own, so the heavier customer aggregate never delays or breaks the overview.</summary>
    public Task<ApiResult<DashboardCustomerLocationDto>> GetCustomersByLocationAsync(DashboardOverviewRequestDto request, CancellationToken cancellationToken) =>
        GetAsync<DashboardCustomerLocationDto>(WithFilters("api/dashboard/customers-by-location", request), cancellationToken);

    private static string WithFilters(string path, DashboardOverviewRequestDto request)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (request.DateFrom is DateOnly from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.DateTo is DateOnly to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
        if (request.OwnerEmployeeId is Guid ownerId) query["ownerEmployeeId"] = ownerId.ToString();
        if (request.ChannelId is byte channelId) query["channelId"] = channelId.ToString(CultureInfo.InvariantCulture);
        if (request.RequestTypeId is int requestTypeId) query["requestTypeId"] = requestTypeId.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.TicketStatus)) query["ticketStatus"] = request.TicketStatus;
        if (request.PriorityId is byte priorityId) query["priorityId"] = priorityId.ToString(CultureInfo.InvariantCulture);

        return query.Count == 0 ? path : $"{path}?{query}";
    }
}
