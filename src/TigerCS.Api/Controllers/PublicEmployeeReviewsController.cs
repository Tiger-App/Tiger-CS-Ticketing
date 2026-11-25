using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The server side of the customer review tablet. Anonymous by design — the customer has no login — so the device token in the
/// <c>X-Device-Token</c> header is the only credential. It travels in a header, not the URL or body, so it never reaches request
/// logs, and it identifies the <i>tablet</i>: the employee being reviewed is resolved here from the tablet's current assignment and
/// can never be supplied by the caller.
/// </summary>
[ApiController]
[Route("api/public/employee-reviews")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
[Tags(OpenApiTags.EmployeeReviews)]
[NoPermissionRequired("Anonymous customer review tablet: the device token is the credential.")]
public class PublicEmployeeReviewsController(EmployeeReviewTabletService tabletService) : ControllerBase
{
    public const string TokenHeader = "X-Device-Token";
    public const string RateLimitPolicy = "employee-review-tablet";

    /// <summary>Checks the tablet's credential and returns the names to show (tablet and employee). Read-only apart from the tablet's "last seen" time.</summary>
    /// <response code="200">The session's status — <c>Ready</c>, <c>Inactive</c> or <c>Invalid</c> — and, when ready, the tablet and employee names.</response>
    /// <response code="429">Too many requests from this tablet.</response>
    [HttpPost("session")]
    [ProducesResponseType<TabletSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Session(CancellationToken cancellationToken) =>
        Ok(await tabletService.GetSessionAsync(Request.Headers[TokenHeader].ToString(), cancellationToken));

    /// <summary>Submits one review: a 1–5 rating, an optional comment (at most 1000 characters) and a client-generated <c>submissionId</c> that makes a retry harmless.</summary>
    /// <response code="200">Submitted, or already recorded (the same submission id arrived before — nothing changed).</response>
    /// <response code="400">The rating, comment or submission id is not valid.</response>
    /// <response code="401">The device token is not valid (unknown, revoked or replaced).</response>
    /// <response code="403">The tablet, or its employee, is not active.</response>
    /// <response code="429">Too many requests from this tablet.</response>
    [HttpPost("submit")]
    [ProducesResponseType<SubmitEmployeeReviewResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<SubmitEmployeeReviewResultDto>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<SubmitEmployeeReviewResultDto>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<SubmitEmployeeReviewResultDto>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] SubmitEmployeeReviewRequestDto request, CancellationToken cancellationToken)
    {
        var result = await tabletService.SubmitAsync(Request.Headers[TokenHeader].ToString(), request, cancellationToken);
        return result.Outcome switch
        {
            SubmitEmployeeReviewOutcome.Submitted or SubmitEmployeeReviewOutcome.AlreadyRecorded => Ok(result),
            SubmitEmployeeReviewOutcome.InvalidDevice => StatusCode(StatusCodes.Status401Unauthorized, result),
            SubmitEmployeeReviewOutcome.InactiveDevice => StatusCode(StatusCodes.Status403Forbidden, result),
            _ => BadRequest(result)
        };
    }
}
