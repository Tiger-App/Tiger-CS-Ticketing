using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.QueueingIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Queueing integration administration - System Administrator only. The one action here is the audited <b>reprocess</b> of an event that is stuck (a Failed event, or a
/// held one whose cause an administrator has fixed). TigerCS is a one-way mirror: this never sends anything to the Queueing System and never skips or alters an event.
/// </summary>
[Route("api/admin/queueing")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminQueueingController(QueueingEventProcessor processor) : AdminControllerBase
{
    /// <summary>
    /// Reprocess one event: its retry cycle restarts (the attempt counter is reset; the payload and the EventId never change), it is applied again under the ticket's lock and, if
    /// it settles, the events held behind it are released in sequence order. Refused when the event is not Failed or Unresolved. Written to the audit trail with the administrator, the
    /// event and what it was before. There is deliberately no skip: an event that cannot be applied stays blocking until its cause is fixed.
    /// </summary>
    [HttpPost("events/{eventId}/reprocess")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<QueueingReprocessResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reprocess(string eventId, CancellationToken cancellationToken)
    {
        var result = await processor.ReprocessAsync(CallerEmployeeId, eventId, cancellationToken);
        return result switch
        {
            null => NotFound(),
            { Status: "NotReprocessable" } => Problem(
                type: "https://tigercs.internal/problems/queueing-event-not-reprocessable",
                title: "The event cannot be reprocessed",
                detail: $"The event is {result.PreviousStatus}; only a Failed or Unresolved event can be reprocessed.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Ok(result)
        };
    }
}
