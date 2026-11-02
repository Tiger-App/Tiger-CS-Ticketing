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
/// Business-rule change: searches by the intake's own phone number and
/// returns whatever each searched source found — enrichment/identification
/// for the agent, never a Ticket creation gate. Which sources are searched
/// depends on the intake's DepartmentId: when set, only that Department's
/// configured source(s) (<see cref="TigerCS.Domain.Modules.Ticketing.DepartmentCustomerLookupSource"/>)
/// are searched — never all three by default, and never falling back to an
/// unconfigured source; when absent, all of CRM, PACT, and Tasleeh are
/// searched. Scoped to CS Agent/CS Supervisor only
/// (PolicyNames.CustomerVerification), same rationale as
/// IntakeRecordsController/CrmController: this is a step of the same
/// intake-then-create sequence those endpoints belong to.
/// </summary>
[ApiController]
[Route("api/intake-records/{intakeRecordId:long}/customer-lookup")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.CustomerLookup)]
[RequirePermission(Permissions.TicketsCreate)]
public class CustomerLookupController(CustomerLookupAppService customerLookupAppService) : ControllerBase
{
    /// <summary>Search the intake's Department-configured source(s) — or all of CRM/PACT/Tasleeh if no Department was selected — for its phone number.</summary>
    /// <remarks>
    /// Always 200, with one entry per source actually searched — unsearched
    /// sources (because the intake's Department is not configured for them)
    /// have no entry at all, never a fake NotFound. A searched source that
    /// found nothing (NotFound) or could not be reached (Failed) never hides
    /// another source's match, and this call never blocks or gates ticket
    /// creation (<c>POST /api/tickets</c>). A Found source carries 0..N
    /// matched customers, each with 0..N units — pass whichever unit's
    /// unitReferenceId/contactReferenceId the agent selected straight to
    /// ticket creation to link it; PACT/Tasleeh matches are display-only
    /// (their units list is always empty).
    /// </remarks>
    /// <param name="intakeRecordId">The intake record whose phone number (and optional DepartmentId, to narrow which sources are searched) to search with.</param>
    /// <response code="200">Each source actually searched, one entry per source, each Found/NotFound/Failed.</response>
    /// <response code="404">No such intake record.</response>
    [HttpGet]
    [ProducesResponseType<CustomerLookupResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(long intakeRecordId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerLookupAppService.SearchAsync(intakeRecordId, cancellationToken);
        return result.Outcome switch
        {
            CustomerLookupOutcome.Success => Ok(result.Response),
            _ => NotFound()
        };
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
