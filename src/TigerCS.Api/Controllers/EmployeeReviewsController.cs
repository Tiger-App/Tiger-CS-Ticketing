using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The Employee Reviews dashboard. Every figure is computed server-side over the caller's own visibility, resolved from their roles and
/// department memberships (management: everyone; CS Supervisor: employees of their departments; anyone else: only themselves). Filters
/// can only narrow that scope. Dates are UAE calendar days; every timestamp is returned in UTC and in UAE time.
/// </summary>
[ApiController]
[Route("api/employee-reviews")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.EmployeeReviews)]
[RequirePermission(Permissions.EmployeeReviewsView)]
public class EmployeeReviewsController(EmployeeReviewQueryAppService queries) : ControllerBase
{
    /// <summary>Totals, average, five-star distribution, reviews per employee and recent customer comments for the filters (default: the last 30 UAE days).</summary>
    /// <response code="200">The overview, limited to the employees the caller may see.</response>
    [HttpGet]
    [ProducesResponseType<EmployeeReviewsOverviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview([FromQuery] EmployeeReviewFilterRequestDto request, CancellationToken cancellationToken)
    {
        if (CallerId() is not { } caller)
        {
            return Unauthorized();
        }

        return Ok(await queries.GetOverviewAsync(caller, Roles(), request, cancellationToken));
    }

    /// <summary>One employee's review history and rating breakdown.</summary>
    /// <response code="200">The employee's reviews for the period.</response>
    /// <response code="403">The employee is outside the caller's scope.</response>
    [HttpGet("employees/{employeeId:guid}")]
    [ProducesResponseType<EmployeeReviewDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Employee(Guid employeeId, [FromQuery] EmployeeReviewFilterRequestDto request, CancellationToken cancellationToken)
    {
        if (CallerId() is not { } caller)
        {
            return Unauthorized();
        }

        var result = await queries.GetEmployeeAsync(caller, Roles(), employeeId, request, cancellationToken);
        return result.Outcome == EmployeeReviewQueryOutcome.Success ? Ok(result.Value) : Forbid();
    }

    private Guid? CallerId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private IReadOnlyCollection<string> Roles() => [.. User.FindAll(ClaimTypes.Role).Select(c => c.Value)];
}
