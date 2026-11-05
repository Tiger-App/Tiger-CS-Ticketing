using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The name and e-mail an agent enters by hand for a customer — the New Ticket "manual entry" form and the Customer Details
/// "Edit name" action. Tiger Ticketing's own data only: nothing here is sent to Tiger CRM, PACT or Tasleeh, and a ticket's
/// verification status, unit association and phone number are never touched. Scoped like the intake/lookup steps it belongs to
/// (CS Agent / CS Supervisor with <c>Tickets.Create</c>).
/// </summary>
[ApiController]
[Route("api/customers/contact")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.CustomerDirectory)]
[RequirePermission(Permissions.TicketsCreate)]
public class CustomerContactController(CustomerContactAppService customerContactAppService) : ControllerBase
{
    /// <summary>The saved name/e-mail for a phone number (any written form), or 404 when none was entered.</summary>
    [HttpGet]
    [ProducesResponseType<CustomerContactDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromQuery] string? phoneNumber, CancellationToken cancellationToken)
    {
        var found = await customerContactAppService.GetByPhonesAsync([phoneNumber], cancellationToken);
        return found.Values.FirstOrDefault() is { } contact ? Ok(contact) : NotFound();
    }

    /// <summary>Create the record for this phone, or update the existing one — never a second record for the same number.</summary>
    /// <response code="200">Saved; <c>created</c> says whether a new record was made.</response>
    /// <response code="400">Name missing/invalid, e-mail invalid, or phone missing.</response>
    /// <response code="403">The caller's role cannot write.</response>
    [HttpPut]
    [ProducesResponseType<CustomerContactDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Save([FromBody] SaveCustomerContactRequestDto request, CancellationToken cancellationToken)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return Unauthorized();
        }

        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var result = await customerContactAppService.SaveAsync(employeeId, roles, request, cancellationToken);
        switch (result.Outcome)
        {
            case CustomerContactOutcome.Success:
                return Ok(result.Response);
            case CustomerContactOutcome.Forbidden:
                return Forbid();
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
}
