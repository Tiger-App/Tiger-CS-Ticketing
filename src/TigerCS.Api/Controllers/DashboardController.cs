using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The operational Dashboard (Customer Workspace phase): KPI counts and the
/// Tickets Requiring Attention list. One read-only aggregate — every number
/// is computed server-side over the caller's own visible-department scope,
/// resolved from their roles/department membership exactly as the ticket
/// queue resolves it (<see cref="TicketsController.GetQueue"/>), never from
/// anything client-supplied. Open to all authenticated staff: the response
/// only ever summarizes tickets the caller could already list.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.Dashboard)]
[RequirePermission(Permissions.DashboardView)]
public class DashboardController(DashboardAppService dashboardAppService) : ControllerBase
{
    /// <summary>The caller's operational dashboard: KPI counts and Tickets Requiring Attention, scoped to their visible departments.</summary>
    /// <response code="200">The dashboard summary. Counts cover only tickets the caller may view.</response>
    [HttpGet]
    [ProducesResponseType<DashboardSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await dashboardAppService.GetSummaryAsync(employeeId.Value, GetRoles(), cancellationToken);
        return Ok(result);
    }

    /// <summary>The Operational Dashboard: filters, KPI cards, the six operational breakdowns and the Recent/Critical list, scoped to the caller's visible departments.</summary>
    /// <remarks>
    /// Dashboard Phase 1. Every filter is optional and can only narrow the
    /// caller's visible scope — a department outside it yields empty
    /// numbers, never someone else's. KPIs, Open Backlog Ageing and the
    /// Recent/Critical list are current-state (all active tickets in scope,
    /// whatever their age); the volume breakdowns cover tickets created in
    /// the date range, which defaults to the last 30 UTC calendar days. The
    /// response echoes the filters as applied and the picker options
    /// (departments, agents, channels, request types, statuses, priorities)
    /// drawn from the existing master data within the caller's scope.
    /// </remarks>
    /// <response code="200">The dashboard overview. Counts cover only tickets the caller may view.</response>
    [HttpGet("overview")]
    [ProducesResponseType<DashboardOverviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview([FromQuery] DashboardOverviewRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await dashboardAppService.GetOverviewAsync(employeeId.Value, GetRoles(), request, cancellationToken);
        return Ok(result);
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}
