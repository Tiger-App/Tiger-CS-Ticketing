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
/// Customer interactions waiting for a human agent — the agent work list, on
/// every channel.
///
/// <para>
/// <b>Deliberately not "callbacks".</b> A callback is one way of continuing a
/// phone conversation; this list also carries website chats a bot could not
/// finish, WhatsApp threads, social-media messages and virtual-agent
/// handovers. Naming it after the phone case would have hidden most of it.
/// </para>
///
/// <para>
/// <b>TigerCS shows the work; Genesys does the talking.</b> The actions here
/// are Start, Complete and Cancel — recording what a human is doing about the
/// work. There is deliberately no "Call" or "Reply on WhatsApp" action,
/// because Genesys owns channel delivery and no supported action for it has
/// been confirmed. The agent opens the ticket, which carries the customer, the
/// units, the department, the transcript and the history.
/// </para>
///
/// <para>
/// <b>Completing a work item never closes its ticket.</b> The two lifecycles
/// are separate: the customer whose chat was answered may still have an NOC
/// workflow running for days.
/// </para>
/// </summary>
[ApiController]
[Route("api/pending-customer-interactions")]
// Authorization is resource-level, not attribute-level. DepartmentScoped's
// requirement is resource-based (DepartmentScopedHandler is
// AuthorizationHandler<DepartmentScopedRequirement, int>, so it only runs
// when the authorization call supplies the department id as the resource).
// Attribute authorization supplies the endpoint, never an int, so that
// requirement was never satisfied here and every caller except the
// ADR-0024 override role received 403 — including the CS Agent this work
// list exists for.
//
// The gate that belongs here is the one AgentHandoffAppService already
// applies per row: ResolveVisibleDepartmentIdsAsync scopes the list, and
// CanViewDepartmentAsync checks the specific handoff's own department
// before every mutation. That is evaluated against the handoff's current
// department rather than a claim, which is what
// Security-Architecture.md §3 asks for, and it carries the override
// through AuthorizationGate exactly as every other ticket operation does.
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.PendingCustomerInteractions)]
public class PendingCustomerInteractionsController(AgentHandoffAppService agentHandoffAppService) : ControllerBase
{
    /// <summary>List customer interactions waiting for a human agent.</summary>
    /// <remarks>
    /// Scoped by exactly the same visible-department rule as the ticket queue,
    /// so an agent never sees work on a ticket they could not open. Ordered by
    /// longest wait first.
    ///
    /// <para>
    /// Outstanding work only by default. A ticket that is still
    /// <b>Unclassified</b> appears here like any other: pending human work
    /// never waits for classification — often the agent classifies it
    /// <i>because</i> they picked it up from this list.
    /// </para>
    /// </remarks>
    /// <param name="departmentId">Narrow to one department.</param>
    /// <param name="channelId">Narrow to one channel.</param>
    /// <param name="assignedEmployeeId">Narrow to one agent's own work.</param>
    /// <param name="unassignedOnly">Only work nobody has taken yet.</param>
    /// <param name="includeResolved">Include completed and cancelled work.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page (1-200, default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The work list.</response>
    [HttpGet]
    [RequirePermission(Permissions.InteractionsView)]
    [ProducesResponseType<AgentHandoffListResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int? departmentId,
        [FromQuery] byte? channelId,
        [FromQuery] Guid? assignedEmployeeId,
        [FromQuery] bool unassignedOnly,
        [FromQuery] bool includeResolved,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await agentHandoffAppService.ListAsync(
            employeeId.Value, GetRoles(),
            new AgentHandoffListRequestDto(
                departmentId, channelId, assignedEmployeeId, unassignedOnly, includeResolved,
                page == 0 ? 1 : page, pageSize == 0 ? 50 : pageSize),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Accept a waiting customer interaction — take the work, own the ticket, and start.</summary>
    /// <remarks>
    /// <b>One indivisible act.</b> The work item is claimed exclusively, the
    /// ticket's owner becomes the accepting employee, and an Open ticket moves
    /// to In Progress — all in one transaction, or none of it.
    ///
    /// <para>
    /// <b>The accepting employee must be an active member of the ticket's
    /// current department</b>, because accepting makes them its owner and an
    /// owner must belong to that department. A non-member receives <c>422</c>
    /// and <b>nothing changes</b>: the interaction is still waiting, unclaimed,
    /// and the ticket is still Open and unowned. To handle work from another
    /// department, transfer the ticket first and then have a member of its new
    /// current department accept.
    /// </para>
    ///
    /// <para>
    /// Idempotent for the holder: accepting work you already hold changes
    /// nothing and does not move the recorded start time. A different agent
    /// receives <c>409</c> naming who holds it.
    /// </para>
    ///
    /// <para>
    /// Accepting is <b>not</b> replying, so it does not satisfy the First
    /// Response SLA — the first actual message a human sends the customer
    /// does.
    /// </para>
    /// </remarks>
    /// <param name="handoffId">The work item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The work is yours and in progress. Repeating the call is idempotent.</response>
    /// <response code="403">The caller may not act on work in this department.</response>
    /// <response code="404">No such work item.</response>
    /// <response code="409">Another agent already holds this work (the response names them), the work was already completed or cancelled, the ticket behind it is closed, or the ticket moved underneath the request.</response>
    /// <response code="422">The caller is not an active member of the ticket's current department, so they cannot become its owner. Nothing was changed.</response>
    [HttpPost("{handoffId:long}/start")]
    [RequirePermission(Permissions.InteractionsEdit)]
    [ProducesResponseType<AgentHandoffDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Start(long handoffId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        return Respond(await agentHandoffAppService.AcceptAsync(employeeId.Value, GetRoles(), handoffId, cancellationToken));
    }

    /// <summary>Record that the human work on this interaction is finished.</summary>
    /// <remarks>
    /// <b>This does not close, resolve or otherwise touch the ticket.</b> The
    /// ticket keeps following the existing TigerCS workflow — the two statuses
    /// are read side by side, never conflated.
    /// </remarks>
    /// <param name="handoffId">The work item.</param>
    /// <param name="request">An optional note on what was done.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The work is complete. The ticket is unchanged.</response>
    /// <response code="403">The caller may not act on work in this department.</response>
    /// <response code="404">No such work item.</response>
    /// <response code="409">The work was already completed or cancelled.</response>
    [HttpPost("{handoffId:long}/complete")]
    [RequirePermission(Permissions.InteractionsEdit)]
    [ProducesResponseType<AgentHandoffDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(
        long handoffId, [FromBody] CompleteAgentHandoffRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        return Respond(await agentHandoffAppService.CompleteAsync(
            employeeId.Value, GetRoles(), handoffId, request, cancellationToken));
    }

    /// <summary>Stand down pending human work that is no longer needed.</summary>
    /// <remarks>
    /// A reason is required — pending customer work is never dropped without a
    /// recorded why. Note that a customer disconnecting is <b>not</b> a reason
    /// to cancel: the live session ended, the business case did not.
    /// </remarks>
    /// <param name="handoffId">The work item.</param>
    /// <param name="request">The required reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The work is cancelled. The ticket is unchanged.</response>
    /// <response code="403">The caller may not act on work in this department.</response>
    /// <response code="404">No such work item.</response>
    /// <response code="409">The work was already completed or cancelled.</response>
    /// <response code="422">No reason was given.</response>
    [HttpPost("{handoffId:long}/cancel")]
    [RequirePermission(Permissions.InteractionsEdit)]
    [ProducesResponseType<AgentHandoffDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(
        long handoffId, [FromBody] CancelAgentHandoffRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        return Respond(await agentHandoffAppService.CancelAsync(
            employeeId.Value, GetRoles(), handoffId, request, cancellationToken));
    }

    private IActionResult Respond(AgentHandoffResult result) => result.Outcome switch
    {
        AgentHandoffOutcome.Success => Ok(result.Handoff),

        AgentHandoffOutcome.NotFound => Problem(
            type: "https://tigercs.internal/problems/pending-interaction-not-found",
            title: "No such pending customer interaction",
            statusCode: StatusCodes.Status404NotFound),

        AgentHandoffOutcome.Forbidden => Problem(
            type: "https://tigercs.internal/problems/forbidden",
            title: "Not permitted for this department",
            detail: "Acting on pending customer work requires the same department access as the ticket behind it.",
            statusCode: StatusCodes.Status403Forbidden),

        AgentHandoffOutcome.AlreadyResolved => Problem(
            type: "https://tigercs.internal/problems/pending-interaction-already-resolved",
            title: "This work was already completed or cancelled",
            detail: "The work item accepts no further changes. Nothing was written.",
            statusCode: StatusCodes.Status409Conflict),

        // Exclusive claim: the loser is told WHO holds it. The holder's
        // employee id is already visible to anyone who can see this work list
        // (the list itself carries AssignedEmployeeId), so naming them here
        // discloses nothing new and turns a mystery refusal into an
        // actionable one.
        AgentHandoffOutcome.AlreadyClaimed => Problem(
            type: "https://tigercs.internal/problems/pending-interaction-already-claimed",
            title: "Another agent already took this interaction",
            detail:
                $"Agent {result.HolderEmployeeId} claimed it at "
                + $"{result.ClaimedAtUtc?.ToString("O") ?? "an earlier time"}. Nothing was written.",
            statusCode: StatusCodes.Status409Conflict),

        // Not 403: the caller may well be entitled to see and act on this
        // department's work. What they cannot do is become the ticket's owner,
        // which is the same 422 TicketsController answers for
        // EmployeeNotInDepartment on the assignment endpoint — deliberately the
        // same problem type, because it is the same invariant.
        AgentHandoffOutcome.AgentNotInTicketDepartment => Problem(
            type: "https://tigercs.internal/problems/employee-not-in-department",
            title: "Employee not in department",
            detail:
                "Accepting a customer interaction makes you the ticket's owner, and an owner must be an "
                + "active member of the ticket's current department. Nothing was changed — the interaction is "
                + "still waiting and the ticket is untouched. Transfer the ticket to your department first, "
                + "or ask a member of its current department to take it.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        AgentHandoffOutcome.TicketClosed => Problem(
            type: "https://tigercs.internal/problems/ticket-closed",
            title: "The ticket behind this work is closed",
            detail:
                "A closed ticket takes neither an owner nor a status change, so this interaction cannot be "
                + "accepted. Stand it down instead. Nothing was changed.",
            statusCode: StatusCodes.Status409Conflict),

        AgentHandoffOutcome.ConcurrencyConflict => Problem(
            type: "https://tigercs.internal/problems/concurrency-conflict",
            title: "The ticket changed while this was being saved",
            detail: "Nothing was written. Reload the interaction and try again.",
            statusCode: StatusCodes.Status409Conflict),

        AgentHandoffOutcome.ReasonRequired => Problem(
            type: "https://tigercs.internal/problems/pending-interaction-reason-required",
            title: "A reason is required",
            detail: "Pending customer work is never cancelled without a recorded why.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    private string[] GetRoles() => User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}
