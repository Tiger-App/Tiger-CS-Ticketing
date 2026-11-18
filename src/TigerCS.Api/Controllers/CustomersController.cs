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
    CustomerDirectoryAppService customerDirectoryAppService, CustomerProfileEditAppService customerProfileEditAppService) : ControllerBase
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
    [HttpPut("profile/{customerKey}")]
    [RequirePermission(Permissions.CustomersEdit)]
    [ProducesResponseType<CustomerProfileEditedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
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
