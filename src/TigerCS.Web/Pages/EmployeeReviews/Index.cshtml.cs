using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.EmployeeReviews;

/// <summary>
/// The Employee Reviews dashboard: totals, average, five-star distribution, reviews per employee and the latest customer comments, with
/// date / employee / department / star filters kept in the URL (a plain GET form, like Team Performance). Every number comes from
/// <c>GET /api/employee-reviews</c>, already limited to what the signed-in employee may see; the page computes nothing of its own.
/// </summary>
public sealed class IndexModel(EmployeeReviewsApiClient api) : PageModel
{
    public EmployeeReviewsOverviewDto? Overview { get; private set; }

    public ApiOutcome Outcome { get; private set; }

    public EmployeeReviewFilterRequestDto Filter { get; private set; } = new(null, null, null, null, null);

    public bool HasFilters => Filter.EmployeeId is not null || Filter.DepartmentId is not null || Filter.Rating is not null
        || Filter.DateFrom is not null || Filter.DateTo is not null;

    public async Task OnGetAsync(
        DateOnly? dateFrom, DateOnly? dateTo, Guid? employeeId, int? departmentId, int? rating, CancellationToken cancellationToken)
    {
        Filter = new EmployeeReviewFilterRequestDto(dateFrom, dateTo, employeeId, departmentId, rating);
        var result = await api.GetOverviewAsync(Filter, cancellationToken);
        Outcome = result.Outcome;
        Overview = result.IsSuccess ? result.Value : null;
    }

    public string PeriodLabel => Overview is null ? string.Empty : $"{Overview.Filters.DateFrom:MMM d} – {Overview.Filters.DateTo:MMM d, yyyy}";

    /// <summary>The query that carries today's filters to the drill-down, so Back returns to the same view.</summary>
    public string DrillQuery()
    {
        var parts = new List<string>();
        if (Overview is { } o)
        {
            parts.Add("dateFrom=" + o.Filters.DateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            parts.Add("dateTo=" + o.Filters.DateTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (Filter.Rating is { } r) parts.Add("rating=" + r.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    public static string LoadFailureMessage(ApiOutcome outcome) => outcome switch
    {
        ApiOutcome.Forbidden or ApiOutcome.Unauthorized => "You do not have permission to view employee reviews.",
        ApiOutcome.Unreachable or ApiOutcome.BadGateway or ApiOutcome.ServiceUnavailable => "Tiger Ticketing System could not reach the ticketing service. Try again shortly.",
        _ => "Employee reviews could not be loaded."
    };

    public static string Stars(int rating) => new string('★', Math.Clamp(rating, 0, 5)) + new string('☆', 5 - Math.Clamp(rating, 0, 5));

    public static string Average(double? average) => average is { } a ? a.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    /// <summary>The stylesheet class that draws a share of the bar — never an inline style (the CSP forbids it).</summary>
    public static string WidthClass(int count, int total) =>
        "bar-fill--w" + (total <= 0 ? 0 : Math.Clamp((int)Math.Round(100.0 * count / total, MidpointRounding.AwayFromZero), 0, 100)).ToString(CultureInfo.InvariantCulture);
}
