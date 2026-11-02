using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/users")]
[Tags(OpenApiTags.Users)]
public class UsersController(
    UserProfileAppService userProfileAppService,
    UserActivationAppService userActivationAppService,
    AssignableUserAppService assignableUserAppService)
    : ControllerBase
{
    /// <summary>
    /// The cross-department assignee directory: every active employee with
    /// their roles and all their department memberships. CS Manager (and
    /// General Manager, who shares the policy) only; Chairman/CEO is read-only and refused — the roles
    /// that may place a ticket outside its current department.
    /// </summary>
    /// <remarks>
    /// Used by Ticket Details when the ticket's current department holds no
    /// suitable assignee: choosing a user from another department transfers
    /// the ticket to that department and assigns it in one operation
    /// (<c>POST /api/tickets/{id}/transfer</c> with <c>assignToEmployeeId</c>).
    /// The rule that the assignee belongs to the ticket's resulting
    /// department is enforced there, not here.
    /// </remarks>
    /// <response code="200">Every active employee, ordered by display name, one row each.</response>
    /// <response code="403">The caller may not assign across departments.</response>
    [HttpGet("assignable")]
    [RequirePermission(Permissions.TicketsAssign)]
    [Authorize(Policy = PolicyNames.CsManagerOrGeneralManager)]
    [ProducesResponseType<IReadOnlyList<AssignableUserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Assignable(CancellationToken cancellationToken) =>
        Ok(await assignableUserAppService.ListAsync(cancellationToken));

    /// <summary>The signed-in user's own profile.</summary>
    /// <remarks>
    /// Resolved from the access token's subject claim, never from a
    /// client-supplied id. MVP-API-Contracts.md §1.3.
    /// </remarks>
    /// <response code="200">The caller's employee id, display name, roles, and department memberships.</response>
    [HttpGet("me")]
    [NoPermissionRequired("Self-service: every signed-in user reads their own profile.")]
    [AllowServiceIdentity]
    [ProducesResponseType<CurrentUserResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return Unauthorized();
        }

        var profile = await userProfileAppService.GetCurrentUserAsync(employeeId, cancellationToken);
        return profile is null ? Unauthorized() : Ok(profile);
    }

    /// <summary>Activate or deactivate a user. System Administrator only.</summary>
    /// <remarks>MVP-API-Contracts.md §1.6.</remarks>
    /// <param name="employeeId">The employee to activate or deactivate.</param>
    /// <param name="request">The desired activation state, and an optional reason.</param>
    /// <response code="200">Activation updated. Returns the new state and how many of that user's open tickets were affected.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such employee.</response>
    /// <response code="409">Refused: this is the last active System Administrator.</response>
    [HttpPatch("{employeeId:guid}/activation")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [Authorize(Policy = PolicyNames.SystemAdministrator)]
    [ProducesResponseType<ActivationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetActivation(
        Guid employeeId, [FromBody] ActivationRequestDto request, CancellationToken cancellationToken)
    {
        var result = await userActivationAppService.SetActivationAsync(employeeId, request, cancellationToken);

        return result.Outcome switch
        {
            ActivationOutcome.Success => Ok(result.Response),
            ActivationOutcome.NotFound => NotFound(),
            ActivationOutcome.LastActiveAdministrator => Problem(
                type: "https://tigercs.internal/problems/last-admin",
                title: "Cannot deactivate the last active System Administrator",
                statusCode: StatusCodes.Status409Conflict),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
