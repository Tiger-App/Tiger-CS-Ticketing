using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// <b>A Tiger CS Ticketing endpoint — not a CRM endpoint.</b> Customer/
/// requester verification (selecting the requester, recording the
/// verification method/result, deciding whether ticket creation is
/// allowed, the immutable verification-time snapshot, and the audit/
/// authorization/expiry rules) is Tiger CS Ticketing's own business logic;
/// Tiger CRM is consulted only as a read-only data source via
/// <c>CrmUnitLookupAppService</c>/<c>ICrmGateway</c> before this endpoint is
/// ever called. See <c>VerificationSessionAppService</c>'s remarks for the
/// full ownership boundary — that is where this controller's logic
/// actually lives; this class stays a thin HTTP adapter over it, same as
/// every other controller in this codebase.
///
/// <para>
/// MVP-API-Contracts.md §2.4, simplified per MVP-Implementation-Backlog.md
/// §0.2/S-07 into a single combined create+select+confirm call — see
/// VerificationSessionAppService's remarks for the full rationale. Scoped to
/// CS Agent/CS Supervisor only (PolicyNames.CustomerVerification) — see
/// CrmController's remarks for why this is narrower than the contract's
/// literal "Agent and above" wording.
/// </para>
/// </summary>
[ApiController]
[Route("api/verification-sessions")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.CustomerVerification)]
[RequirePermission(Permissions.TicketsCreate)]
public class VerificationSessionsController(VerificationSessionAppService verificationSessionAppService) : ControllerBase
{
    /// <summary>Create and confirm a verification session in one call.</summary>
    /// <remarks>
    /// The combined create+select+confirm call described in
    /// MVP-Implementation-Backlog.md §0.2/S-07. The unit and contact must
    /// already have been looked up through <c>GET /api/crm/units/...</c>.
    /// <para>
    /// Send an optional <c>Idempotency-Key</c> header so a retried request
    /// does not create a second session.
    /// </para>
    /// </remarks>
    /// <param name="request">The unit/contact selection and the confirmation.</param>
    /// <response code="201">The confirmed session, including the verification-time snapshot of the unit and contact.</response>
    /// <response code="400">confirmed was not true, verificationMethod was not one of ManualAgentConfirmation, AuthenticatedDigitalUser, FaceToFaceDocumentCheck, Other, or it was <c>Otp</c> — which only the server-side OTP challenge flow may produce.</response>
    /// <response code="404">unitReferenceId/contactReferenceId do not reference an already-looked-up unit and one of its contacts.</response>
    [HttpPost]
    [ProducesResponseType<VerificationSessionResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(
        [FromBody] CreateVerificationSessionRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
        {
            ModelState.AddModelError(nameof(request.Confirmed), "Confirmed must be true.");
            return ValidationProblem(ModelState);
        }

        // Names only: Enum.TryParse would also accept a numeric string such as "3".
        if (!Enum.GetNames<VerificationMethod>().Contains(request.VerificationMethod, StringComparer.Ordinal))
        {
            ModelState.AddModelError(
                nameof(request.VerificationMethod),
                $"VerificationMethod must be one of: {string.Join(", ", Enum.GetNames<VerificationMethod>())}.");
            return ValidationProblem(ModelState);
        }

        var agentEmployeeId = GetEmployeeId();
        if (agentEmployeeId is null)
        {
            return Unauthorized();
        }

        var result = await verificationSessionAppService.CreateAndConfirmAsync(
            agentEmployeeId.Value, request, idempotencyKey, cancellationToken);

        return result.Outcome switch
        {
            VerificationSessionOutcome.Success =>
                Created($"/api/verification-sessions/{result.Response!.VerificationSessionId}", result.Response),
            VerificationSessionOutcome.OtpRequiresChallenge => Problem(
                type: "https://tigercs.internal/problems/otp-requires-challenge",
                title: "OTP verification can only be completed through the OTP challenge flow",
                detail: "verificationMethod \"Otp\" is proof TigerCS produces itself, not a value a caller may assert. "
                    + "Use POST /api/genesys/verification/otp/send and /otp/verify.",
                statusCode: StatusCodes.Status400BadRequest),
            VerificationSessionOutcome.UnitOrContactNotFound => Problem(
                type: "https://tigercs.internal/problems/unit-or-contact-not-found",
                title: "Unit or contact not found",
                detail: "UnitReferenceId/ContactReferenceId must reference an already-looked-up unit and one of its contacts.",
                statusCode: StatusCodes.Status404NotFound),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Fetch one verification session. Owner-only.</summary>
    /// <remarks>
    /// Owner-only (single-agent ownership, MVP-ERD.md §2.24) — used to
    /// resume state, or to hand a confirmed session's data to ticket creation.
    /// </remarks>
    /// <param name="verificationSessionId">The session to fetch.</param>
    /// <response code="200">The session. status is one of InProgress, Confirmed, Consumed, Expired, Abandoned.</response>
    /// <response code="403">The session belongs to another agent.</response>
    /// <response code="404">No such session.</response>
    [HttpGet("{verificationSessionId:guid}")]
    [ProducesResponseType<VerificationSessionResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid verificationSessionId, CancellationToken cancellationToken)
    {
        var callerEmployeeId = GetEmployeeId();
        if (callerEmployeeId is null)
        {
            return Unauthorized();
        }

        var result = await verificationSessionAppService.GetAsync(verificationSessionId, callerEmployeeId.Value, cancellationToken);

        return result.Outcome switch
        {
            VerificationSessionOutcome.Success => Ok(result.Response),
            VerificationSessionOutcome.NotFound => NotFound(),
            VerificationSessionOutcome.Forbidden => Forbid(),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
