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
/// Item 1 of this increment's scope: capture every customer interaction as
/// an IntakeRecord before any verification is attempted (MVP-ERD.md §2.9),
/// so no request — unit-related or not (item 2) — is ever silently lost.
/// Scoped to CS Agent/CS Supervisor only (PolicyNames.CustomerVerification),
/// same rationale as VerificationSessionsController/CrmController: this is
/// the first step of the same verify-then-create sequence those endpoints
/// gate, and no role that cannot go on to create a ticket has a documented
/// need to record an intake either.
/// </summary>
[ApiController]
[Route("api/intake-records")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.Intake)]
[RequirePermission(Permissions.TicketsCreate)]
public class IntakeRecordsController(IntakeRecordAppService intakeRecordAppService) : ControllerBase
{
    /// <summary>Record a customer interaction, before any customer lookup is attempted.</summary>
    /// <remarks>
    /// The unconditional first step of intake (MVP-ERD.md §2.9): every
    /// interaction is captured, unit-related or not, so none is silently
    /// lost. The phone number captured here is what customer lookup
    /// (<c>GET /api/intake-records/{intakeRecordId}/customer-lookup</c>)
    /// later searches CRM/PACT/Tasleeh with.
    /// </remarks>
    /// <param name="request">The channel, the phone number, an optional department (narrows customer lookup to its configured source(s)), whether the request concerns a unit, and the raw unit number if the caller happened to give one.</param>
    /// <response code="201">The intake record, with its initial crmVerificationStatus.</response>
    /// <response code="400">
    /// channelId did not resolve to a configured channel (by id or code), or
    /// resolved to a deactivated channel; or the channel's configuration
    /// requires a phone number and phoneNumber was blank.
    /// </response>
    /// <response code="404">departmentId was supplied but does not reference a real department.</response>
    [HttpPost]
    [ProducesResponseType<IntakeRecordResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateIntakeRecordRequestDto request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            ModelState.AddModelError(nameof(request.ChannelId), "Required.");
            return ValidationProblem(ModelState);
        }

        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await intakeRecordAppService.CreateAsync(employeeId.Value, request, cancellationToken);
        return result.Outcome switch
        {
            IntakeRecordOutcome.Success => Created($"/api/intake-records/{result.Response!.IntakeRecordId}", result.Response),
            IntakeRecordOutcome.ChannelNotFound => ValidationFailure(
                nameof(request.ChannelId), "ChannelId does not reference a configured channel."),
            IntakeRecordOutcome.ChannelInactive => ValidationFailure(
                nameof(request.ChannelId), "The selected channel is inactive and cannot be used for a new ticket."),
            IntakeRecordOutcome.PhoneNumberRequired => ValidationFailure(
                nameof(request.PhoneNumber), "A phone number is required for the selected channel."),
            IntakeRecordOutcome.DepartmentNotFound => Problem(
                type: "https://tigercs.internal/problems/department-not-found",
                title: "Department not found",
                statusCode: StatusCodes.Status404NotFound),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private IActionResult ValidationFailure(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
