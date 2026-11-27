using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.Integrations;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.QueueingIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The receiving side of the Queueing System integration. The Queueing System posts every change to a QR-code queue ticket here; TigerCS mirrors it onto
/// one TigerCS ticket. <b>The Queueing System is the source of truth</b> for the lifecycle of those tickets.
/// </summary>
/// <remarks>
/// Authenticated with the Queueing System's own API key (<c>X-Queueing-Api-Key</c>), independent of staff sign-in: the key opens this endpoint only.
/// </remarks>
[ApiController]
[Route("api/integrations/queueing")]
[Authorize(AuthenticationSchemes = QueueingApiKeyDefaults.Scheme, Policy = PolicyNames.QueueingIntegration)]
[Tags(OpenApiTags.QueueingIntegration)]
[NoPermissionRequired("Queueing System integration surface; authenticated by its own API key, never by a staff account, so no staff permission applies.")]
public class QueueingIntegrationController(
    QueueingEventProcessor processor,
    TigerCS.Application.Modules.QueueingIntegration.Abstractions.IQueueingStore store,
    TigerCS.Application.Modules.QueueingIntegration.QueueingIntegrationOptions options,
    TimeProvider timeProvider) : ControllerBase
{
    /// <summary>Health of the integration: events per status, held / failed events by outcome, the blocked queue tickets, and the ones that need attention (no customer data).</summary>
    /// <remarks>Read-only and authenticated with the same API key as the receiver, so the Queueing System's monitoring can read TigerCS's side too. An event that is
    /// <c>Unresolved</c> is retried by TigerCS (mapping or employee missing - indefinitely, at a capped backoff); <c>Failed</c> exhausted its bounded attempts and needs an
    /// administrator's reprocess. Every later event of a blocked ticket waits behind its head event (<c>blockedTickets</c>); alert on <c>alertCount &gt; 0</c>.</remarks>
    [HttpGet("monitoring")]
    [ProducesResponseType<QueueingMonitoringSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Monitoring([FromQuery] int take = 50, CancellationToken cancellationToken = default) =>
        Ok(await store.GetMonitoringSnapshotAsync(
            timeProvider.GetUtcNow().UtcDateTime, take, TimeSpan.FromMinutes(Math.Max(1, options.BlockedAlertAfterMinutes)), cancellationToken));

    /// <summary>Receive one Queueing System event.</summary>
    /// <remarks>
    /// <b>Idempotent on <c>eventId</c></b> (a unique database index): a redelivery returns the original outcome and changes nothing. One TigerCS ticket per
    /// <c>externalTicketId</c> (also a unique index). A state-changing event older than the ticket's applied state (<c>eventSequence</c>) is recorded and
    /// never applied. An event that cannot be applied yet (its ticket was not created yet, a company / ticket type / employee mapping is missing) is stored
    /// and retried by TigerCS: the answer is 202 and the Queueing System need not resend it.
    /// </remarks>
    /// <response code="200">Applied, already applied (a duplicate), superseded by a newer event, or rejected as not applicable - see <c>status</c>.</response>
    /// <response code="201">The TigerCS ticket was created for this queue ticket.</response>
    /// <response code="202">Stored, not applied yet - see <c>outcome</c> and <c>detail</c>. TigerCS retries it.</response>
    /// <response code="400">The event failed validation (per-event required fields). Nothing was stored.</response>
    /// <response code="401">Missing or invalid API key.</response>
    /// <response code="409">The same <c>eventId</c> was already received with different content.</response>
    /// <response code="503">The integration is switched off, or another request is changing the same ticket (safe to resend; <c>Retry-After</c> is set).</response>
    [HttpPost("events")]
    [ProducesResponseType<QueueingEventResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<QueueingEventResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<QueueingEventResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<QueueingEventResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<QueueingEventResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<QueueingEventResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive([FromBody] QueueingEventRequest request, CancellationToken cancellationToken)
    {
        var response = await processor.ReceiveAsync(request, cancellationToken);

        switch (response.Status)
        {
            case QueueingResultStatus.Invalid:
                return BadRequest(response);
            case QueueingResultStatus.Conflict:
                return Conflict(response);
            case QueueingResultStatus.Pending:
                return StatusCode(StatusCodes.Status202Accepted, response);
            case QueueingResultStatus.Disabled:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, response);
            case QueueingResultStatus.Busy:
                Response.Headers.RetryAfter = "2";
                return StatusCode(StatusCodes.Status503ServiceUnavailable, response);
            default:
                return response.CreatedTicket
                    ? Created($"/api/tickets/{response.TicketId}", response)
                    : Ok(response);
        }
    }
}
