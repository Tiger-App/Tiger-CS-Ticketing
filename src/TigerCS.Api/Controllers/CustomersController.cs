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
/// The Customers directory: the customers TigerCS already knows, derived from
/// its own persisted tickets (never a CRM list call — no integrated system
/// offers one), and the profile behind each. Same visibility scope as the
/// ticket queue: a customer is only reachable through tickets the caller may
/// see.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.CustomerDirectory)]
[RequirePermission(Permissions.CustomersView)]
public class CustomersController(
    CustomerDirectoryAppService customerDirectoryAppService,
    CustomerProfileEditAppService customerProfileEditAppService,
    CustomerPropertyAppService customerPropertyAppService,
    CustomerChangeHistoryAppService customerChangeHistoryAppService) : ControllerBase
{
    /// <summary>Lists known customers, most recently active first — paginated, optionally searched and filtered.</summary>
    [HttpGet]
    [ProducesResponseType<CustomerDirectoryListResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] CustomerDirectoryListRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerDirectoryAppService.ListAsync(employeeId.Value, GetRoles(), request, cancellationToken);
        return Ok(result);
    }

    /// <summary>Read-only: the Unknown customers of the same query as the list and the Dashboard's Customers by Location card, counted by why they are Unknown.</summary>
    [HttpGet("unknown-breakdown")]
    [ProducesResponseType<CustomerLocationBreakdownDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UnknownBreakdown([FromQuery] CustomerDirectoryListRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        return Ok(await customerDirectoryAppService.UnknownBreakdownAsync(employeeId.Value, GetRoles(), request, cancellationToken));
    }

    /// <summary>One customer's profile by directory key (<c>crm:{id}</c>, <c>ext:{source}:{externalCustomerId}</c>, <c>phone:{number}</c>).</summary>
    [HttpGet("profile/{customerKey}")]
    [ProducesResponseType<CustomerDirectoryProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(string customerKey, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerDirectoryAppService.GetProfileAsync(employeeId.Value, GetRoles(), customerKey, cancellationToken);
        return result.Outcome switch
        {
            CustomerDirectoryProfileOutcome.Success => Ok(result.Response),
            CustomerDirectoryProfileOutcome.InvalidKey => Problem(statusCode: StatusCodes.Status400BadRequest, title: "The customer key is not valid."),
            _ => Problem(statusCode: StatusCodes.Status404NotFound, title: "No customer with that key is visible to you."),
        };
    }

    /// <summary>
    /// Corrects the phone number, project and unit a customer is shown with in the directory. Local to Tiger Ticketing: nothing is sent to
    /// Tiger CRM, PACT or Tasleeh, no ticket changes, and the customer's identity, tickets and history are untouched. Blank clears that
    /// edit (the tickets' own value is shown again). Needs <c>Customers.Edit</c>: System Administrator and CS Manager, or an explicit
    /// grant. The phone is normalized to its international form; project and unit are entered together and checked against each other
    /// where the known projects allow. Audited with the customer, the values before and after, and who changed them.
    /// </summary>
    /// <response code="200">Saved (or nothing to change); <c>changed</c> says which.</response>
    /// <response code="400">The key, phone, project or unit is not valid; <c>errors</c> names the field.</response>
    /// <response code="403">The caller may not edit customers.</response>
    /// <response code="404">No customer with that key is visible to the caller.</response>
    /// <response code="409">Someone else saved this customer first; reload and try again.</response>
    [HttpPut("profile/{customerKey}")]
    [RequirePermission(Permissions.CustomersEdit)]
    [ProducesResponseType<CustomerProfileEditedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateProfile(
        string customerKey, [FromBody] UpdateCustomerProfileRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerProfileEditAppService.UpdateAsync(employeeId.Value, GetRoles(), customerKey, request, cancellationToken);
        switch (result.Outcome)
        {
            case CustomerProfileEditOutcome.Success:
                return Ok(result.Response);
            case CustomerProfileEditOutcome.Forbidden:
                return Forbid();
            case CustomerProfileEditOutcome.InvalidKey:
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The customer key is not valid.");
            case CustomerProfileEditOutcome.NotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "No customer with that key is visible to you.");
            case CustomerProfileEditOutcome.Conflict:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "This customer was changed by someone else. Reload and try again.");
            default:
                foreach (var (field, messages) in result.Errors ?? new Dictionary<string, string[]>())
                {
                    foreach (var message in messages)
                    {
                        ModelState.AddModelError(field, message);
                    }
                }

                return ValidationProblem(ModelState);
        }
    }


    /// <summary>
    /// Adds a property / unit to a customer by hand, with the relationship Owner, Tenant or Representative. Stored in Tiger Ticketing only and
    /// always shown as "Manually added": it is never CRM or PACT ownership and nothing is written to either. Needs <c>Customers.Edit</c>.
    /// </summary>
    /// <response code="200">Saved; <c>changed</c> is false for a retry of an operation already recorded.</response>
    /// <response code="400">The key, project, unit or relationship is not valid.</response>
    /// <response code="404">No customer with that key is visible to the caller.</response>
    /// <response code="409">The customer already has that project and unit (manually added or verified), or someone saved first.</response>
    [HttpPost("profile/{customerKey}/properties")]
    [RequirePermission(Permissions.CustomersEdit)]
    [ProducesResponseType<CustomerPropertyResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddProperty(string customerKey, [FromBody] CustomerPropertyRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        return employeeId is null
            ? Unauthorized()
            : PropertyResponse(await customerPropertyAppService.AddAsync(employeeId.Value, GetRoles(), customerKey, request, cancellationToken));
    }

    /// <summary>Changes the project, unit or relationship of a manually added property. Verified units cannot be changed here. Needs <c>Customers.Edit</c>.</summary>
    [HttpPut("profile/{customerKey}/properties/{associationId:long}")]
    [RequirePermission(Permissions.CustomersEdit)]
    [ProducesResponseType<CustomerPropertyResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateProperty(
        string customerKey, long associationId, [FromBody] CustomerPropertyRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        return employeeId is null
            ? Unauthorized()
            : PropertyResponse(await customerPropertyAppService.UpdateAsync(employeeId.Value, GetRoles(), customerKey, associationId, request, cancellationToken));
    }

    /// <summary>Removes a manually added property. Only the manual association is removed - a verified unit can never be removed here, and nothing is changed in CRM or PACT. Needs <c>Customers.Edit</c>.</summary>
    [HttpDelete("profile/{customerKey}/properties/{associationId:long}")]
    [RequirePermission(Permissions.CustomersEdit)]
    [ProducesResponseType<CustomerPropertyResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveProperty(
        string customerKey, long associationId, [FromQuery] string? reason, [FromQuery] long? relatedTicketId, [FromQuery] Guid? correlationId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        return employeeId is null
            ? Unauthorized()
            : PropertyResponse(await customerPropertyAppService.RemoveAsync(
                employeeId.Value, GetRoles(), customerKey, associationId,
                new CustomerPropertyRequestDto(null, null, null, reason, relatedTicketId, correlationId), cancellationToken));
    }

    /// <summary>
    /// A customer's change history, newest first: which field changed, from what to what, who changed it, when, from where and why. Holds
    /// personal data, so it needs <c>Customers.ViewHistory</c> (System Administrator and CS Manager, or an explicit grant). Read-only: there
    /// is no endpoint to edit or delete a history entry.
    /// </summary>
    /// <response code="200">One page of history; empty for a customer who has never been edited.</response>
    /// <response code="403">The caller may not view customer change history.</response>
    /// <response code="404">No customer with that key is visible to the caller.</response>
    [HttpGet("profile/{customerKey}/history")]
    [RequirePermission(Permissions.CustomersViewHistory)]
    [ProducesResponseType<CustomerChangeHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(string customerKey, [FromQuery] int page = 1, [FromQuery] int pageSize = CustomerChangeHistoryAppService.DefaultPageSize, CancellationToken cancellationToken = default)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerChangeHistoryAppService.ListAsync(employeeId.Value, GetRoles(), customerKey, page, pageSize, cancellationToken);
        return result.Outcome switch
        {
            CustomerChangeHistoryOutcome.Success => Ok(result.Response),
            CustomerChangeHistoryOutcome.InvalidKey => Problem(statusCode: StatusCodes.Status400BadRequest, title: "The customer key is not valid."),
            _ => Problem(statusCode: StatusCodes.Status404NotFound, title: "No customer with that key is visible to you."),
        };
    }

    private IActionResult PropertyResponse(CustomerPropertyResult result)
    {
        switch (result.Outcome)
        {
            case CustomerPropertyOutcome.Success:
                return Ok(result);
            case CustomerPropertyOutcome.Forbidden:
                return Forbid();
            case CustomerPropertyOutcome.InvalidKey:
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The customer key is not valid.");
            case CustomerPropertyOutcome.NotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "No such customer or property is visible to you.");
            case CustomerPropertyOutcome.Duplicate:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "This customer already has that project and unit.");
            case CustomerPropertyOutcome.Conflict:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "This customer was changed by someone else. Reload and try again.");
            default:
                foreach (var (field, messages) in result.Errors ?? new Dictionary<string, string[]>())
                {
                    foreach (var message in messages)
                    {
                        ModelState.AddModelError(field, message);
                    }
                }

                return ValidationProblem(ModelState);
        }
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
}
