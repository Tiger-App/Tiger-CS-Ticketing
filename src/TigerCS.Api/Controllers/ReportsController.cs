using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Management reports. Read-only, and restricted to the CS Manager tier
/// (<see cref="PolicyNames.ReportsRead"/>: CS Manager, General
/// Manager, Chairman/CEO — plus System Administrator through the ADR-0024
/// override). A CS Agent or Department user receives 403; the report is
/// about agents, not for them. Nothing here is scoped by the caller's
/// departments: the roles that may open it already see every department.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = PolicyNames.ReportsRead)]
[Tags(OpenApiTags.Reports)]
[RequirePermission(Permissions.ReportsView)]
public class ReportsController(TeamPerformanceAppService teamPerformanceAppService) : ControllerBase
{
    /// <summary>The Team Performance report: one row per active CS Agent with Currently Assigned, Tickets Worked, Completed Follow-ups and SLA Breaches, a totals row and the picker options.</summary>
    /// <remarks>
    /// Eligible employees are the active holders of the CS Agent role; the
    /// agent type is "Call Center Agent" for members of the Call Center
    /// department (code CC) and "CS Agent" otherwise. Every filter is
    /// optional. The period is UTC calendar days, inclusive, defaulting to
    /// the last 30 days; Currently Assigned is current-state and ignores it.
    /// Employees with no activity in the period are included with zeros.
    /// </remarks>
    /// <response code="200">The report.</response>
    /// <response code="403">The caller is not a CS Manager, General Manager, Chairman/CEO or System Administrator.</response>
    [HttpGet("team-performance")]
    [ProducesResponseType<TeamPerformanceReportDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTeamPerformance([FromQuery] TeamPerformanceRequestDto request, CancellationToken cancellationToken)
    {
        var report = await teamPerformanceAppService.GetReportAsync(request, cancellationToken);
        return Ok(report);
    }

    /// <summary>The records behind one count on the Team Performance report, for one employee: the ticket list, with the breach time for SLA Breaches and the completion time for Completed Follow-ups.</summary>
    /// <remarks>
    /// <c>metric</c> is one of CurrentlyAssigned, TicketsWorked,
    /// CompletedFollowUps, SlaBreaches. The period is interpreted exactly as
    /// on the report, so the list always matches the number it was opened
    /// from.
    /// </remarks>
    /// <response code="200">The records.</response>
    /// <response code="400">The metric is not one of the four counts.</response>
    /// <response code="404">The employee is not on the report (not an active CS Agent).</response>
    [HttpGet("team-performance/records")]
    [ProducesResponseType<TeamPerformanceRecordsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeamPerformanceRecords([FromQuery] TeamPerformanceRecordsRequestDto request, CancellationToken cancellationToken)
    {
        var result = await teamPerformanceAppService.GetRecordsAsync(request, cancellationToken);
        return result.Outcome switch
        {
            TeamPerformanceRecordsOutcome.Success => Ok(result.Records),
            TeamPerformanceRecordsOutcome.UnknownMetric => Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unknown metric.",
                detail: "metric must be one of CurrentlyAssigned, TicketsWorked, CompletedFollowUps, SlaBreaches."),
            TeamPerformanceRecordsOutcome.EmployeeNotEligible => Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Employee not on the report.",
                detail: "The employee is not an active holder of a reported role."),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
