using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Reporting.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/reports</c> endpoints — the CS Manager's
/// Team Performance report and the records behind one of its counts. The
/// Api decides who may read them (403 otherwise); this client only relays
/// the outcome so the page can show a "no permission" state.
/// </summary>
public sealed class ReportsApiClient(HttpClient httpClient, ILogger<ReportsApiClient> logger)
    : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<TeamPerformanceReportDto>> GetTeamPerformanceAsync(TeamPerformanceRequestDto request, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (request.DateFrom is DateOnly from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.DateTo is DateOnly to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.EmployeeId is Guid employeeId) query["employeeId"] = employeeId.ToString();
        if (!string.IsNullOrWhiteSpace(request.Role)) query["role"] = request.Role;
        if (request.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);

        return GetAsync<TeamPerformanceReportDto>(
            query.Count == 0 ? "api/reports/team-performance" : $"api/reports/team-performance?{query}", cancellationToken);
    }

    public Task<ApiResult<TeamPerformanceRecordsDto>> GetTeamPerformanceRecordsAsync(TeamPerformanceRecordsRequestDto request, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["employeeId"] = request.EmployeeId.ToString();
        query["metric"] = request.Metric;
        if (request.DateFrom is DateOnly from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.DateTo is DateOnly to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return GetAsync<TeamPerformanceRecordsDto>($"api/reports/team-performance/records?{query}", cancellationToken);
    }

    public Task<ApiResult<TeamPerformanceSlaDetailsDto>> GetTeamPerformanceSlaDetailsAsync(TeamPerformanceSlaDetailsRequestDto request, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["employeeId"] = request.EmployeeId.ToString();
        if (request.DateFrom is DateOnly from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.DateTo is DateOnly to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.Role)) query["role"] = request.Role;
        if (request.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.Status)) query["status"] = request.Status;
        if (!string.IsNullOrWhiteSpace(request.SlaType)) query["slaType"] = request.SlaType;
        if (!string.IsNullOrWhiteSpace(request.SortBy)) query["sortBy"] = request.SortBy;
        if (!string.IsNullOrWhiteSpace(request.SortDir)) query["sortDir"] = request.SortDir;
        query["page"] = request.Page.ToString(CultureInfo.InvariantCulture);
        query["pageSize"] = request.PageSize.ToString(CultureInfo.InvariantCulture);

        return GetAsync<TeamPerformanceSlaDetailsDto>($"api/reports/team-performance/sla-details?{query}", cancellationToken);
    }
}
