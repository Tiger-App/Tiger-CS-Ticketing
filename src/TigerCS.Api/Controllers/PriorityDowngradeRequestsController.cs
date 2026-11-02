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
/// Priority-downgrade requests (MVP-API-Contracts.md section 5.6.1-5.6.5,
/// ISSUE-023 Option B, Finding DR-05).
///
/// <para>
/// A downgrade never takes effect on request: <c>POST</c> creates a Pending
/// request and leaves the ticket's priority and SLA period untouched. Only
/// <c>approve</c>, performed by a Department Head of the ticket's current
/// department (or CS Manager / General Manager, or a System Administrator via
/// the central override), changes the priority. The approver is always the
/// authenticated caller; no request body carries one.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[RequireTicketAccess]
[Tags(OpenApiTags.SlaAndEscalation)]
public class PriorityDowngradeRequestsController(PriorityDowngradeAppService service) : ControllerBase
{
    /// <summary>Request a priority downgrade. The ticket is unchanged until a Department Head approves.</summary>
    /// <remarks>
    /// Section 5.6.1. `newPriorityId` must be a genuine decrease (a numerically larger id) and `reason` is
    /// required. One Pending request per ticket: a second returns 409 with the existing request.
    /// A request does not modify the ticket, so no `rowVersion` is needed (deviation from the contract's If-Match, documented).
    /// </remarks>
    /// <response code="201">The Pending request.</response>
    /// <response code="400">Reason missing or the priority is unknown.</response>
    /// <response code="403">The caller may not request a priority change on this ticket.</response>
    /// <response code="404">No such ticket.</response>
    /// <response code="409">`downgrade-request-already-pending`; the body carries the existing request.</response>
    /// <response code="422">Not a downgrade, ticket unclassified, or ticket Resolved/Closed.</response>
    [HttpPost("api/tickets/{ticketId:long}/sla/priority-downgrade-requests")]
    [RequirePermission(Permissions.TicketsEdit)]
    [ProducesResponseType<PriorityDowngradeRequestResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RequestDowngrade(
        long ticketId, [FromBody] CreateDowngradeRequestRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } employeeId)
        {
            return Unauthorized();
        }

        var result = await service.RequestAsync(employeeId, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result, r => Created($"/api/tickets/{ticketId}/sla/priority-downgrade-requests", r));
    }

    /// <summary>A ticket's downgrade-request history, newest first (Pending/Approved/Rejected/Expired).</summary>
    /// <response code="200">The requests.</response>
    /// <response code="403">The ticket is outside the caller's department visibility.</response>
    /// <response code="404">No such ticket.</response>
    [HttpGet("api/tickets/{ticketId:long}/sla/priority-downgrade-requests")]
    [RequirePermission(Permissions.TicketsView)]
    [ProducesResponseType<IReadOnlyList<PriorityDowngradeRequestResponseDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListForTicket(long ticketId, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } employeeId)
        {
            return Unauthorized();
        }

        return ToActionResult(await service.ListForTicketAsync(employeeId, GetRoles(), ticketId, cancellationToken), r => Ok(r));
    }

    /// <summary>The Department Head's inbox of requests awaiting a decision.</summary>
    /// <remarks>Section 5.6.3. A Department Head sees only their own department(s); CS Manager / General Manager / System Administrator see all, optionally filtered by `departmentId`.</remarks>
    /// <response code="200">A page of Pending, unexpired requests with ticket context.</response>
    /// <response code="403">The caller holds no decision authority, or asked for a department they do not belong to.</response>
    [HttpGet("api/priority-downgrade-requests/pending")]
    [RequirePermission(Permissions.TicketsApprove)]
    [Authorize(Policy = PolicyNames.DepartmentHeadOrAbove)]
    [ProducesResponseType<PendingDowngradePageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListPending(
        [FromQuery] int? departmentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (GetEmployeeId() is not { } employeeId)
        {
            return Unauthorized();
        }

        return ToActionResult(
            await service.ListPendingAsync(employeeId, GetRoles(), departmentId, page, pageSize, cancellationToken), r => Ok(r));
    }

    /// <summary>Approve a pending downgrade. The only path by which a downgrade takes effect.</summary>
    /// <remarks>
    /// Section 5.6.4. Atomically: the ticket's priority changes, the current SLA period ends and a `Downgrade`
    /// period opens (approver recorded from the caller's own identity). Breach flags already recorded are never
    /// cleared. The requester cannot approve their own request.
    /// </remarks>
    /// <response code="200">The decided request and the newly opened SLA period.</response>
    /// <response code="403">Not a Department Head of the ticket's current department, or self-approval.</response>
    /// <response code="404">No such request.</response>
    /// <response code="409">`downgrade-request-not-pending`, stale priority, or a lost concurrency race.</response>
    /// <response code="410">The request expired.</response>
    /// <response code="422">The ticket is Closed or Resolved.</response>
    [HttpPost("api/priority-downgrade-requests/{requestId:long}/approve")]
    [RequirePermission(Permissions.TicketsApprove)]
    [Authorize(Policy = PolicyNames.DepartmentHeadOrAbove)]
    [ProducesResponseType<DowngradeDecisionResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Approve(
        long requestId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ApproveDowngradeRequestRequestDto? body,
        CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } employeeId)
        {
            return Unauthorized();
        }

        return ToActionResult(await service.ApproveAsync(employeeId, GetRoles(), requestId, body, cancellationToken), r => Ok(r));
    }

    /// <summary>Reject a pending downgrade; the ticket's priority is unchanged.</summary>
    /// <remarks>Section 5.6.5. `decisionNote` is required.</remarks>
    /// <response code="200">The Rejected request.</response>
    /// <response code="400">Decision note missing.</response>
    /// <response code="403">Not a Department Head of the ticket's current department, or self-decision.</response>
    /// <response code="404">No such request.</response>
    /// <response code="409">Not pending, or a lost concurrency race.</response>
    /// <response code="410">The request expired.</response>
    [HttpPost("api/priority-downgrade-requests/{requestId:long}/reject")]
    [RequirePermission(Permissions.TicketsApprove)]
    [Authorize(Policy = PolicyNames.DepartmentHeadOrAbove)]
    [ProducesResponseType<PriorityDowngradeRequestResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<IActionResult> Reject(
        long requestId, [FromBody] RejectDowngradeRequestRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } employeeId)
        {
            return Unauthorized();
        }

        return ToActionResult(await service.RejectAsync(employeeId, GetRoles(), requestId, request, cancellationToken), r => Ok(r));
    }

    private IActionResult ToActionResult<T>(DowngradeResult<T> result, Func<T, IActionResult> onSuccess) => result.Outcome switch
    {
        DowngradeOutcome.Success => onSuccess(result.Response!),

        DowngradeOutcome.NotFound => NotFound(),
        DowngradeOutcome.Forbidden => Forbid(),

        DowngradeOutcome.InvalidRequest => Problem(
            type: "https://tigercs.internal/problems/invalid-request",
            title: "Invalid request",
            detail: "A reason (or decision note) is required and the priority must exist.",
            statusCode: StatusCodes.Status400BadRequest),

        DowngradeOutcome.NotADowngrade => Problem(
            type: "https://tigercs.internal/problems/not-a-downgrade",
            title: "Not a downgrade",
            detail: "The requested priority is not lower than the current one. A priority increase is a separate operation.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        DowngradeOutcome.TicketNotClassified => Problem(
            type: "https://tigercs.internal/problems/ticket-not-classified",
            title: "Ticket is not classified",
            detail: "The ticket has no priority yet; classify it instead. Setting a first priority is not a downgrade.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        DowngradeOutcome.TicketFinal => Problem(
            type: "https://tigercs.internal/problems/ticket-priority-final",
            title: "Ticket priority is final",
            detail: "A Resolved or Closed ticket's priority can no longer change.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        DowngradeOutcome.AlreadyPending => Conflict(new ProblemDetails
        {
            Type = "https://tigercs.internal/problems/downgrade-request-already-pending",
            Title = "A downgrade request is already pending",
            Detail = "Wait for the pending request to be decided or expire.",
            Status = StatusCodes.Status409Conflict,
            Extensions = { ["existingRequestId"] = result.Existing?.PriorityDowngradeRequestId, ["existingRequest"] = result.Existing }
        }),

        DowngradeOutcome.NotPending => Problem(
            type: "https://tigercs.internal/problems/downgrade-request-not-pending",
            title: "Request is not pending",
            detail: "The request was already decided, expired or superseded.",
            statusCode: StatusCodes.Status409Conflict),

        DowngradeOutcome.Expired => Problem(
            type: "https://tigercs.internal/problems/downgrade-request-expired",
            title: "Request expired",
            detail: "The request is past its expiry; submit a new one if the downgrade is still wanted.",
            statusCode: StatusCodes.Status410Gone),

        DowngradeOutcome.StalePriority => Problem(
            type: "https://tigercs.internal/problems/downgrade-priority-changed",
            title: "Ticket priority changed since the request",
            detail: "The ticket's priority is no longer the one this request was made against. Reject it and submit a new request.",
            statusCode: StatusCodes.Status409Conflict),

        DowngradeOutcome.SelfApprovalForbidden => Problem(
            type: "https://tigercs.internal/problems/downgrade-self-approval",
            title: "Requester cannot decide their own request",
            detail: "A different authorized approver must decide this request.",
            statusCode: StatusCodes.Status403Forbidden),

        DowngradeOutcome.ConcurrencyConflict => Problem(
            type: "https://tigercs.internal/problems/concurrency-conflict",
            title: "Concurrency conflict",
            detail: "The request or ticket changed concurrently. Re-read and retry.",
            statusCode: StatusCodes.Status409Conflict),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}
