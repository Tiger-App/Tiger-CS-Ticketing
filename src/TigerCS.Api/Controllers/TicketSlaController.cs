using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.Filters;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The SLA and Escalation module's client surface (MVP-API-Contracts.md
/// §5.1, §5.2, §5.7, §5.9).
///
/// <para>
/// <b>Automatic escalation is deliberately absent from this controller.</b>
/// §5.7 states it plainly: breach-driven escalation is "not exposed as a
/// client-callable endpoint, since it's system-triggered". It is raised by
/// the background-job path of ADR-0015, inside the same transaction as the
/// breach it responds to.
/// </para>
///
/// <para>
/// <b>Four endpoints of §5 are not implemented, each for a stated reason.</b>
/// §5.3/§5.4 (pause/resume) are out of pilot scope
/// (MVP-Implementation-Backlog.md §0.2 — a disclosed limitation).
/// §5.5 (priority upgrade) is backlog S-14, a separate increment, and the
/// two approved documents disagree on which moment its recomputation runs
/// from — reported rather than guessed. §5.8 (respond to an escalation)
/// specifies its authorization only as "the notified role-holder ... as
/// applicable", which no approved document maps onto the fixed role set, so
/// it is reported as a contract gap rather than implemented against an
/// invented mapping. §5.6's downgrade endpoints are hard-disabled for the
/// pilot (§0). §5.10–§5.12 (business-calendar and holiday administration)
/// are stretch scope (§0.2 — "seeded configuration is committed instead").
/// </para>
/// </summary>
[ApiController]
[Route("api/tickets/{ticketId:long}")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[RequireTicketAccess]
[Tags(OpenApiTags.SlaAndEscalation)]
public class TicketSlaController(
    SlaQueryAppService slaQueryAppService,
    SlaFirstResponseAppService slaFirstResponseAppService,
    TicketEscalationAppService ticketEscalationAppService) : ControllerBase
{
    /// <summary>The ticket's SLA position — due dates, breach flags, and current escalation level.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §5.1. Visible to any caller who can see the
    /// ticket itself; department scoping is resolved server-side from the
    /// caller's own roles and department membership.
    ///
    /// The due-date fields are null only while a provisional ticket is still
    /// awaiting CRM reconciliation — an unverified ticket has not started its
    /// SLA clock (FR-TKT-09), and no deadline is fabricated for it.
    ///
    /// `isCurrentlyPaused`, `currentPauseReason` and
    /// `totalPausedMinutesThisPeriod` are part of the approved response shape
    /// and always return false/null/0 in this pilot: SLA pause/resume is not
    /// built (MVP-Implementation-Backlog.md §0.2).
    /// </remarks>
    /// <param name="ticketId">The ticket to report on.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The ticket's SLA summary.</response>
    /// <response code="403">The ticket is outside the caller's department visibility.</response>
    /// <response code="404">No such ticket.</response>
    [HttpGet("sla")]
    [RequirePermission(Permissions.TicketsView)]
    [ProducesResponseType<TicketSlaSummaryResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSla(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await slaQueryAppService.GetSummaryAsync(employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return ToActionResult(result, r => Ok(r));
    }

    /// <summary>Record that the First Human Response has occurred.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §5.2 / ISSUE-019. This is the only event that
    /// satisfies the First Response SLA — the automated acknowledgement never
    /// does, on any channel.
    ///
    /// Write-once at the ticket level: a second call is answered with 409,
    /// because the target is measured against the *first* engagement and a
    /// later correction would silently move a contractual measurement.
    ///
    /// `occurredAtUtc` may legitimately precede the ticket's own creation
    /// timestamp — a call answered moments before the agent finished creating
    /// the ticket satisfies the SLA at the moment of actual human engagement
    /// (SLA-Architecture.md §16 Example E).
    ///
    /// If the response landed after the deadline, this call also finalizes
    /// the breach flag and raises the automatic Level 2 escalation, exactly
    /// as the scheduled deadline job would have — and never twice, since both
    /// paths share one idempotency key.
    /// </remarks>
    /// <param name="ticketId">The ticket being responded to.</param>
    /// <param name="request">The source of the response and, optionally, when it occurred.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The updated SLA summary.</response>
    /// <response code="400">The request body was malformed, or `source` was not one of Manual/GenesysCallAnswer.</response>
    /// <response code="403">The caller is neither the ticket's owner nor a role authorized to record on its behalf.</response>
    /// <response code="404">No such ticket.</response>
    /// <response code="409">A first response was already recorded, or the supplied `rowVersion` was stale.</response>
    /// <response code="422">The ticket is Closed — a closed ticket accepts no further changes.</response>
    [HttpPost("sla/first-response")]
    [RequirePermission(Permissions.TicketsEscalate)]
    [ProducesResponseType<TicketSlaSummaryResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecordFirstResponse(
        long ticketId, [FromBody] RecordFirstResponseRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await slaFirstResponseAppService.RecordAsync(
            employeeId.Value, GetRoles(), ticketId, request, cancellationToken);

        return ToActionResult(result, r => Ok(r));
    }

    /// <summary>Manually escalate a ticket.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §5.7 / FR-ESC-01.
    ///
    /// Escalation is an independent dimension, not a status: an escalated
    /// ticket stays exactly as Open or InProgress as it was, and nothing
    /// here changes `ticketStatus`, resolves, or closes the ticket
    /// (ADR-0008/ADR-0011).
    ///
    /// Levels only rise. A request for a level at or below the ticket's
    /// current one is answered with 422 rather than quietly de-escalating it.
    ///
    /// Level 4 (Chairman/CEO) is manual-only and restricted to CS Manager and
    /// General Manager. Because `level` and `triggerType` arrive as separate
    /// fields, the pair must also agree: `level: 4` requires
    /// `triggerType: ManualLevel4` and vice versa, so the role gate cannot be
    /// side-stepped by sending Level 4 under the ManualFlag trigger. The
    /// automatic trigger types are system-only and are rejected here.
    /// </remarks>
    /// <param name="ticketId">The ticket to escalate.</param>
    /// <param name="request">The level, trigger type, optional note, and the ticket's current `rowVersion`.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="201">The created escalation record.</response>
    /// <response code="400">The request body was malformed, `triggerType` was not ManualFlag/ManualLevel4, or `level` was outside 1–4.</response>
    /// <response code="403">The caller may not escalate this ticket, or requested ManualLevel4 without being a CS Manager or General Manager.</response>
    /// <response code="404">No such ticket.</response>
    /// <response code="409">The supplied `rowVersion` was stale.</response>
    /// <response code="422">The ticket is Closed, the level is not an advance, or `level` and `triggerType` disagree about Level 4.</response>
    [HttpPost("escalations")]
    [RequirePermission(Permissions.TicketsEscalate)]
    [ProducesResponseType<TicketEscalationResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Escalate(
        long ticketId, [FromBody] ManualEscalationRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketEscalationAppService.EscalateAsync(
            employeeId.Value, GetRoles(), ticketId, request, cancellationToken);

        return ToActionResult(result, r => Created($"/api/tickets/{ticketId}/escalations", r));
    }

    /// <summary>Escalation history for a ticket, most recent first.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §5.9. Includes both manual escalations and the
    /// automatic Level 2 raised on an SLA breach.
    ///
    /// `notifiedRoles` records who the approved recipient matrix says should
    /// be told, and is deliberately distinct from `level` (ADR-0011): a
    /// Critical breach notifying the General Manager does not, by itself,
    /// make the ticket Level 3. Notification *delivery* — email, SMS,
    /// in-app — is outside this pilot's scope; only the intended recipients
    /// are recorded.
    ///
    /// `respondedAtUtc` and `respondingEmployeeId` are always null in this
    /// pilot: the respond action (§5.8) is not implemented — see this
    /// controller's remarks.
    /// </remarks>
    /// <param name="ticketId">The ticket whose escalations to list.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The ticket's escalations, newest first.</response>
    /// <response code="403">The ticket is outside the caller's department visibility.</response>
    /// <response code="404">No such ticket.</response>
    [HttpGet("escalations")]
    [RequirePermission(Permissions.TicketsView)]
    [ProducesResponseType<IReadOnlyList<TicketEscalationResponseDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEscalations(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await slaQueryAppService.ListEscalationsAsync(employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return ToActionResult(result, r => Ok(r));
    }

    /// <summary>Maps one outcome set to HTTP, so every endpoint in this controller answers the same condition the same way.</summary>
    private IActionResult ToActionResult<T>(SlaOperationResult<T> result, Func<T, IActionResult> onSuccess) => result.Outcome switch
    {
        SlaOperationOutcome.Success => onSuccess(result.Response!),

        SlaOperationOutcome.NotFound => NotFound(),

        SlaOperationOutcome.Forbidden => Forbid(),

        SlaOperationOutcome.ConcurrencyConflict => Problem(
            type: "https://tigercs.internal/problems/concurrency-conflict",
            title: "Concurrency conflict",
            detail: "The ticket changed since you read it. Re-read it and retry with the new rowVersion.",
            statusCode: StatusCodes.Status409Conflict),

        SlaOperationOutcome.TicketClosed => Problem(
            type: "https://tigercs.internal/problems/ticket-closed",
            title: "Ticket is closed",
            detail: "A closed ticket cannot be modified.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        SlaOperationOutcome.FirstResponseAlreadyRecorded => Problem(
            type: "https://tigercs.internal/problems/first-response-already-recorded",
            title: "First response already recorded",
            detail: "FirstHumanResponseAtUtc is write-once at the ticket level.",
            statusCode: StatusCodes.Status409Conflict),

        SlaOperationOutcome.InvalidRequest => Problem(
            type: "https://tigercs.internal/problems/invalid-request",
            title: "Invalid request",
            detail: "One of the supplied enum values is not valid for this endpoint.",
            statusCode: StatusCodes.Status400BadRequest),

        SlaOperationOutcome.EscalationLevelNotAnAdvance => Problem(
            type: "https://tigercs.internal/problems/escalation-level-not-an-advance",
            title: "Escalation level is not an advance",
            detail: "A ticket's escalation level only rises; it is never lowered.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        SlaOperationOutcome.EscalationLevelTriggerMismatch => Problem(
            type: "https://tigercs.internal/problems/escalation-level-trigger-mismatch",
            title: "Escalation level and trigger type disagree",
            detail: "Level 4 requires TriggerType ManualLevel4, and ManualLevel4 requires Level 4.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    /// <summary>The caller's roles, from the JWT's own validated role claims — never client-supplied.</summary>
    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}
