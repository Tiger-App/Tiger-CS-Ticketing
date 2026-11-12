using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Reports;

/// <summary>
/// The SLA details behind one employee's SLA Breaches number on the Team
/// Performance report: a summary (total / met / breached / pending /
/// compliance), the SLA rules in force, and a sorted, server-side paged
/// table of every SLA evaluation with the facts it was decided from.
/// Everything comes from <c>GET /api/reports/team-performance/sla-details</c>;
/// the page computes nothing, and the period, employee, role and department
/// travel in the query string exactly as on the report, so the number the
/// manager clicked and the list they land on are produced by the same
/// backend calculation.
/// </summary>
public sealed class TeamPerformanceSlaModel(ReportsApiClient reportsApiClient) : PageModel
{
    // ---- bound state (query string) ----
    public Guid EmployeeId { get; private set; }
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public string? Role { get; private set; }
    public int? DepartmentId { get; private set; }
    public string? Status { get; private set; }
    public string? SortBy { get; private set; }
    public string? SortDir { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int PageSize { get; private set; } = 25;

    public TeamPerformanceSlaDetailsDto? Details { get; private set; }
    public ApiOutcome Outcome { get; private set; }

    public async Task OnGetAsync(
        Guid employeeId, DateOnly? dateFrom, DateOnly? dateTo, string? role, int? departmentId,
        string? status, string? sortBy, string? sortDir, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        EmployeeId = employeeId;
        DateFrom = dateFrom;
        DateTo = dateTo;
        Role = string.IsNullOrWhiteSpace(role) ? null : role;
        DepartmentId = departmentId;
        Status = string.IsNullOrWhiteSpace(status) ? null : status;
        SortBy = string.IsNullOrWhiteSpace(sortBy) ? null : sortBy;
        SortDir = string.IsNullOrWhiteSpace(sortDir) ? null : sortDir;
        PageNumber = Math.Max(1, page ?? 1);
        PageSize = pageSize is > 0 ? pageSize.Value : 25;

        var result = await reportsApiClient.GetTeamPerformanceSlaDetailsAsync(
            new TeamPerformanceSlaDetailsRequestDto(
                EmployeeId, DateFrom, DateTo, Role, DepartmentId, Status, SortBy, SortDir, PageNumber, PageSize),
            cancellationToken);
        Outcome = result.Outcome;
        Details = result.IsSuccess ? result.Value : null;
    }

    /// <summary>The effective period, as the Api applied it.</summary>
    public string PeriodLabel => Details is null ? string.Empty : $"{Details.DateFrom:MMM d} – {Details.DateTo:MMM d, yyyy}";

    /// <summary>Back to the report under the same period, role and department filters.</summary>
    public string ReportHref
    {
        get
        {
            var query = HttpUtility.ParseQueryString(string.Empty);
            if (DateFrom is DateOnly from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (DateTo is DateOnly to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(Role)) query["role"] = Role;
            if (DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
            return query.Count == 0 ? "/Reports/TeamPerformance" : $"/Reports/TeamPerformance?{query}";
        }
    }

    /// <summary>
    /// A link to this page with some of the state changed. <paramref name="status"/>
    /// is the status tab (<c>""</c> = all); changing the tab or the sort
    /// returns to page 1.
    /// </summary>
    public string Href(int? page = null, string? sortBy = null, string? sortDir = null, string? status = null)
    {
        var effectiveStatus = status is null ? Details?.StatusFilter : (status.Length == 0 ? null : status);
        var effectiveSortBy = sortBy ?? Details?.SortBy;
        var effectiveSortDir = sortDir ?? Details?.SortDir;

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["employeeId"] = EmployeeId.ToString();
        if (DateFrom is DateOnly from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTo is DateOnly to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(Role)) query["role"] = Role;
        if (DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
        if (effectiveStatus is not null) query["status"] = effectiveStatus;
        if (effectiveSortBy is not null) query["sortBy"] = effectiveSortBy;
        if (effectiveSortDir is not null) query["sortDir"] = effectiveSortDir;
        query["page"] = (page ?? 1).ToString(CultureInfo.InvariantCulture);
        query["pageSize"] = (Details?.PageSize ?? PageSize).ToString(CultureInfo.InvariantCulture);
        return $"/Reports/TeamPerformanceSla?{query}";
    }

    /// <summary>The link for a sortable column heading: sorts by it, flipping the direction when it is already the sort column.</summary>
    public string SortHref(string key)
    {
        var current = Details?.SortBy == key;
        var direction = current && Details?.SortDir == "desc" ? "asc" : "desc";
        return Href(page: 1, sortBy: key, sortDir: direction);
    }

    public string? AriaSort(string key) =>
        Details?.SortBy != key ? null : (Details.SortDir == "asc" ? "ascending" : "descending");

    /// <summary>"2026-09-10 14:05" — every timestamp on the page is UTC and formatted the same way.</summary>
    public static string Stamp(DateTime? value) =>
        value is DateTime v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string StatusCss(string status) => status switch
    {
        TeamPerformanceSlaStatuses.Breached => "breached",
        TeamPerformanceSlaStatuses.Met => "met",
        _ => "running"
    };

    public static string ComplianceText(TeamPerformanceSlaSummaryDto summary) => TeamPerformanceModel.ComplianceText(summary.CompliancePercent);

    /// <summary>The empty status: every evaluation, whatever its status.</summary>
    public const string AllStatuses = "";

    public string LoadFailureMessage => Outcome switch
    {
        ApiOutcome.Forbidden => "You do not have permission to view this report. It is available to CS Managers, General Managers and the Chairman/CEO.",
        ApiOutcome.NotFound => "This employee is not on the report under the selected filters.",
        ApiOutcome.Unauthorized => "Your session is no longer valid. Sign in again.",
        ApiOutcome.Unreachable or ApiOutcome.BadGateway => "Tiger Ticketing System could not reach the ticketing service.",
        _ => "The SLA details could not be loaded right now."
    };
}
