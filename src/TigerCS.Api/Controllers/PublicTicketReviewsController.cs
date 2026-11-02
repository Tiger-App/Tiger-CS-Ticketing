using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.CustomerReviews.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The server side of the public customer satisfaction review page. Anonymous by design — the customer arrives from an email
/// link — so the review token in the request body is the only credential, and everything is validated here, never trusted from
/// the page. The token travels in the body (not the URL) so it does not end up in request logs.
/// </summary>
[ApiController]
[Route("api/public/ticket-reviews")]
[AllowAnonymous]
[Tags(OpenApiTags.CustomerReviews)]
[NoPermissionRequired("Anonymous customer review links.")]
public class PublicTicketReviewsController(TicketReviewAppService reviewService) : ControllerBase
{
    /// <summary>Checks a review link. Read-only: it never records a review, so link scanners that open it change nothing.</summary>
    /// <response code="200">The link's status (<c>Valid</c>, <c>Expired</c>, <c>AlreadySubmitted</c> or <c>Invalid</c>); the ticket number only for a genuine link.</response>
    [HttpPost("lookup")]
    [ProducesResponseType<ReviewLinkDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Lookup([FromBody] ReviewTokenRequestDto request, CancellationToken cancellationToken) =>
        Ok(await reviewService.GetLinkAsync(request.Token, cancellationToken));

    /// <summary>Submits the review: a 1–5 rating and an optional comment. Accepted once per ticket; a repeat never changes the stored review.</summary>
    /// <response code="200">The outcome — <c>Submitted</c>, <c>Invalid</c>, <c>Expired</c>, <c>AlreadySubmitted</c>, <c>InvalidRating</c> or <c>InvalidComment</c>.</response>
    [HttpPost("submit")]
    [ProducesResponseType<SubmitReviewResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit([FromBody] SubmitTicketReviewRequestDto request, CancellationToken cancellationToken) =>
        Ok(await reviewService.SubmitAsync(request.Token, new SubmitReviewRequestDto(request.Rating, request.Comment), cancellationToken));
}
