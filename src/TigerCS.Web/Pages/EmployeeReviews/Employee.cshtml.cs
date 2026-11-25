using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.EmployeeReviews;

/// <summary>One employee's review history and rating breakdown. The Api refuses (403) an employee outside the viewer's scope.</summary>
public sealed class EmployeeModel(EmployeeReviewsApiClient api) : PageModel
{
    public EmployeeReviewDetailDto? Detail { get; private set; }

    public ApiOutcome Outcome { get; private set; }

    public EmployeeReviewFilterRequestDto Filter { get; private set; } = new(null, null, null, null, null);

    public async Task OnGetAsync(Guid employeeId, DateOnly? dateFrom, DateOnly? dateTo, int? rating, CancellationToken cancellationToken)
    {
        Filter = new EmployeeReviewFilterRequestDto(dateFrom, dateTo, null, null, rating);
        var result = await api.GetEmployeeAsync(employeeId, Filter, cancellationToken);
        Outcome = result.Outcome;
        Detail = result.IsSuccess ? result.Value : null;
    }

    public string PeriodLabel => Detail is null ? string.Empty : $"{Detail.Filters.DateFrom:MMM d} – {Detail.Filters.DateTo:MMM d, yyyy}";
}
