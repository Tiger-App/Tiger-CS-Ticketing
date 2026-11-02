using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.Filters;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Items 3–8 of this increment's scope, plus a later business-rule change:
/// a single ticket-creation path for every IntakeRecord. Customer
/// information from CRM, PACT, or Tasleeh is attached when available
/// (<see cref="CustomerLookupController"/>); lack of a match never blocks
/// <see cref="Create"/> — the only thing every ticket requires is a valid
/// Ticket Category. Scoped to CS Agent/CS Supervisor only
/// (PolicyNames.CustomerVerification) — same rationale as
/// VerificationSessionsController/CrmController: the Solution-Analysis.md
/// §4.1 permission matrix grants ticket-Create to exactly these two roles.
/// </summary>
[ApiController]
[Route("api/tickets")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[RequireTicketAccess]
[Tags(OpenApiTags.Tickets)]
public class TicketsController(
    TicketCreationAppService ticketCreationAppService,
    TicketQueryAppService ticketQueryAppService,
    TicketAssignmentAppService ticketAssignmentAppService,
    TicketLifecycleAppService ticketLifecycleAppService,
    TicketNoteAppService ticketNoteAppService,
    TicketReconciliationAppService ticketReconciliationAppService,
    CustomerHistoryAppService customerHistoryAppService,
    CustomerProfileAppService customerProfileAppService,
    TicketApprovalAppService ticketApprovalAppService,
    TicketInteractionQueryAppService ticketInteractionQueryAppService,
    TigerCS.Application.Modules.CustomerReviews.Services.TicketReviewQueryAppService ticketReviewQueryAppService,
    TicketClassificationAppService ticketClassificationAppService) : ControllerBase
{
    /// <summary>Create a ticket from an IntakeRecord. CS Agent/CS Supervisor only.</summary>
    /// <remarks>
    /// Create a ticket from an IntakeRecord. Customer information from CRM,
    /// PACT, or Tasleeh is attached when available (see
    /// <c>GET /api/intake-records/{intakeRecordId}/customer-lookup</c>);
    /// lack of a match does not prevent ticket creation. Ticket Category is
    /// required for every ticket. Scoped to CS Agent/CS Supervisor
    /// (Solution-Analysis.md §4.1's Create column) — layered on top of the
    /// class-level AuthenticatedStaff policy, not a replacement for it.
    /// </remarks>
    /// <param name="request">The intake record to promote, plus the matched unit/contact (if the agent selected one), category, priority, and summary.</param>
    /// <response code="201">The created ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">The intake record, unit reference, contact reference, category, priority, or the category's routed department was not found (or the department is inactive).</response>
    /// <response code="409">The intake record was already promoted to a ticket, or a ticket-number collision occurred — retry.</response>
    /// <response code="422">UnitReferenceId and ContactReferenceId were not both supplied or both omitted.</response>
    [HttpPost]
    [RequirePermission(Permissions.TicketsCreate)]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    [ProducesResponseType<TicketResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketCreationAppService.CreateAsync(
            employeeId.Value, request, cancellationToken, callerRoles: GetRoles());
        return ToActionResult(result);
    }

    /// <summary>The ticket queue — a filtered, sorted, paged list of tickets visible to the caller.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.2. Department visibility is resolved
    /// server-side from the caller's roles and department assignments
    /// (TicketQueryAppService), never from a client-supplied department
    /// filter alone: <c>departmentId</c> narrows what the caller may already
    /// see, it does not widen it.
    /// </remarks>
    /// <param name="request">
    /// Filtering, sorting, and paging. All filters are optional and combine
    /// with AND. <c>ticketStatus</c> is one of Open, InProgress,
    /// PendingCustomer, Resolved, Closed — the retired PendingThirdParty is
    /// no longer an offered filter, though a historical ticket still reports
    /// it as its own status;
    /// <c>verificationStatus</c> one of Unverified, PendingCrmVerification,
    /// Verified; <c>priorityId</c> 1=Critical, 2=High, 3=Medium, 4=Low.
    /// </param>
    /// <response code="200">A page of ticket summaries, with the total matching count.</response>
    [HttpGet]
    [RequirePermission(Permissions.TicketsView)]
    [AllowServiceIdentity]
    [ProducesResponseType<TicketListResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueue([FromQuery] TicketListRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        if (request.Escalated == true && !TicketQueryAppService.CanReviewEscalations(GetRoles()))
        {
            return Forbid();
        }

        var result = await ticketQueryAppService.GetQueueAsync(employeeId.Value, GetRoles(), request, cancellationToken);
        return Ok(result);
    }

    /// <summary>Full detail for one ticket.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.3. A ticket created with no customer match is
    /// returned with verificationStatus Unverified and a null unit/contact —
    /// never fabricated as Verified. <c>rowVersion</c> from this response is
    /// what the assignment, transfer, status, resolution, close, and
    /// reconciliation calls expect back.
    /// </remarks>
    /// <param name="ticketId">The ticket to fetch.</param>
    /// <response code="200">The ticket.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}")]
    [RequirePermission(Permissions.TicketsView)]
    [AllowServiceIdentity]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketQueryAppService.GetDetailAsync(employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>Customer History — this ticket's customer's other tickets.</summary>
    /// <remarks>
    /// Verified when the ticket has a CrmBuyerCustomerId (keyed by that exact
    /// id, never by phone); otherwise falls back to the phone-number
    /// snapshot recorded on the IntakeRecord this ticket was promoted from,
    /// and is labelled "Unverified" in the response. The current ticket is
    /// always excluded from its own history. Sourced entirely from the
    /// Tickets table — never a live CRM call — and scoped to the same
    /// department visibility as <see cref="GetDetail"/>.
    /// </remarks>
    /// <param name="ticketId">The ticket whose customer's history to fetch.</param>
    /// <param name="limit">Maximum previous tickets to return, newest first. Defaults to 5; capped at 50.</param>
    /// <response code="200">The customer's ticket-count summary and most recent previous tickets (possibly empty).</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}/customer-history")]
    [RequirePermission(Permissions.TicketsView)]
    [Tags(OpenApiTags.CustomerHistory)]
    [ProducesResponseType<CustomerHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCustomerHistory(long ticketId, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerHistoryAppService.GetForTicketAsync(employeeId.Value, GetRoles(), ticketId, limit, cancellationToken);
        return result.Outcome switch
        {
            CustomerHistoryOutcome.Success => Ok(result.Response),
            CustomerHistoryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>Customer Details/Profile — live CRM Overview/Contact Info/Units for this ticket's customer.</summary>
    /// <remarks>
    /// Ticket-anchored exactly like <see cref="GetCustomerHistory"/>: the
    /// identity (CrmBuyerCustomerId) and the department-visibility check both
    /// come from the ticket itself. Delegates the actual CRM search to the
    /// same CrmBuyerLookupAppService the New Ticket wizard uses — this
    /// endpoint duplicates no CRM logic. Previous Tickets on the Customer
    /// Profile page reuses <see cref="GetCustomerHistory"/> unchanged; this
    /// endpoint carries none of that.
    /// </remarks>
    /// <param name="ticketId">The ticket whose customer's profile to fetch.</param>
    /// <response code="200">The customer's profile. <c>status</c> is one of "NotCrmVerified", "Found", "CrmUnavailable", "AmbiguousCustomerMatch", "NotFoundInCrm", "NoPhoneOnRecord" — only "Found" populates name/mobile/email/units, and only when CRM's Buyer is the ticket's own CrmBuyerCustomerId.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}/customer-profile")]
    [RequirePermission(Permissions.TicketsView)]
    [Tags(OpenApiTags.CustomerProfile)]
    [ProducesResponseType<CustomerProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCustomerProfile(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerProfileAppService.GetForTicketAsync(employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return result.Outcome switch
        {
            CustomerProfileOutcome.Success => Ok(result.Response),
            CustomerProfileOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>The ticket's conversation history — every call/chat recorded against it, with transcripts.</summary>
    /// <remarks>
    /// Genesys integration phase 1. Returns each interaction the ticket
    /// accumulated (the originating Genesys call or chat, plus any later
    /// ones) in chronological order, each with its stored transcript for text
    /// channels. An interaction's own <c>status</c> ("Active"/"Ended") is
    /// independent of the ticket's status: a conversation that ended does not
    /// close the ticket. Read-only, and scoped to the same department
    /// visibility as <see cref="GetDetail"/>.
    /// </remarks>
    /// <param name="ticketId">The ticket whose conversation history to fetch.</param>
    /// <response code="200">The ticket's interactions, oldest first, each with its transcript (possibly empty).</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}/interactions")]
    [RequirePermission(Permissions.TicketsView)]
    [Tags(OpenApiTags.Genesys)]
    [ProducesResponseType<TicketInteractionHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInteractions(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketInteractionQueryAppService.GetForTicketAsync(
            employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>The ticket's customer satisfaction reviews — one entry per review cycle (original resolution, then each reopen). Staff only.</summary>
    /// <remarks>
    /// Read from the persisted review store. A cycle is <c>Reviewed</c> only once the customer submitted a rating; a requested but
    /// unanswered cycle is <c>Pending</c> and carries no rating. Test-mode reviews are flagged <c>IsTest</c>. Review tokens are
    /// never returned. Visible to exactly the callers who can see the ticket itself.
    /// </remarks>
    /// <param name="ticketId">The ticket.</param>
    /// <response code="200">The ticket's review cycles (possibly empty).</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}/reviews")]
    [RequirePermission(Permissions.TicketsView)]
    [ProducesResponseType<TigerCS.Application.Modules.CustomerReviews.Dto.TicketReviewsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviews(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketReviewQueryAppService.GetForTicketAsync(
            employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>Classify an Unclassified ticket — record what the customer actually wants.</summary>
    /// <remarks>
    /// A ticket created from an inquiry nobody had read yet carries no
    /// Category at all (never a placeholder one). Once the agent has read the
    /// conversation, this records the real Category, Priority and — where
    /// they can name it — Request Type.
    ///
    /// <para>
    /// <b>The same ticket is classified in place.</b> No second ticket is
    /// created: TCK-1001 keeps its number, department, conversation history
    /// and audit trail, and simply stops being Unclassified.
    /// </para>
    ///
    /// <para>
    /// <b>This is where the SLA clock starts</b> for such a ticket. The SLA
    /// policy is selected by priority, so an unclassified ticket deliberately
    /// had no SLA period rather than one measured against a provisional
    /// value. The period opened here is backdated to the ticket's own
    /// creation time, so classifying late never buys extra time.
    /// </para>
    ///
    /// <para>
    /// A ticket that was already classified is refused with <c>422</c>:
    /// re-categorising an existing ticket has its own SLA consequences and is
    /// a different operation, not built in this phase.
    /// </para>
    /// </remarks>
    /// <param name="ticketId">The unclassified ticket.</param>
    /// <param name="request">The real category, priority, optional request type, and the ticket's current rowVersion.</param>
    /// <response code="200">The now-classified ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The ticket is already classified or closed, or the category/priority/request type is not valid for it.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/classification")]
    [RequirePermission(Permissions.TicketsEdit)]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> Classify(
        long ticketId, [FromBody] ClassifyTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketClassificationAppService.ClassifyAsync(
            employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Assign a ticket to an employee — self-claim or reassign.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.5. Both self-assignment and cross-department
    /// reach are validated inside TicketAssignmentAppService, never trusted
    /// from the request alone.
    /// </remarks>
    /// <param name="ticketId">The ticket to assign.</param>
    /// <param name="request">The employee to assign it to, and the ticket's current rowVersion.</param>
    /// <response code="200">The updated ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/assignment")]
    [RequirePermission(Permissions.TicketsAssign)]
    [Tags(OpenApiTags.Assignment)]
    public async Task<IActionResult> Assign(long ticketId, [FromBody] AssignTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketAssignmentAppService.AssignAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Transfer a ticket to another department.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.6. Clears the current owner — the receiving
    /// department must explicitly claim or assign it.
    /// </remarks>
    /// <param name="ticketId">The ticket to transfer.</param>
    /// <param name="request">The target department, the reason, and the ticket's current rowVersion.</param>
    /// <response code="200">The updated ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/transfer")]
    [RequirePermission(Permissions.TicketsAssign)]
    [Tags(OpenApiTags.Transfer)]
    public async Task<IActionResult> Transfer(long ticketId, [FromBody] TransferTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketAssignmentAppService.TransferAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Change a ticket's working status.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.7 — the "work" sub-machine only
    /// (Open to InProgress, and InProgress to and from PendingCustomer, which
    /// is optional: a ticket may go straight from InProgress to Resolved).
    /// Resolved and Closed are reached only via <see cref="Resolve"/> and
    /// <see cref="Close"/>, and Closed returns to InProgress only via
    /// <see cref="Reopen"/>.
    ///
    /// <para>
    /// PendingThirdParty is a legacy-readable status and is refused as a
    /// target here with the ordinary 422 invalid-transition response, however
    /// the request is shaped. A ticket that was already in it moves back to
    /// InProgress through this same endpoint.
    /// </para>
    /// </remarks>
    /// <param name="ticketId">The ticket to update.</param>
    /// <param name="request">The new status (InProgress or PendingCustomer) and the ticket's current rowVersion.</param>
    /// <response code="200">The updated ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/status")]
    [RequirePermission(Permissions.TicketsEdit)]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> ChangeStatus(long ticketId, [FromBody] ChangeStatusRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketLifecycleAppService.ChangeStatusAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Resolve a ticket. Department Employee/Department Head only.</summary>
    /// <remarks>MVP-API-Contracts.md §3.9, ISSUE-022's approved Resolve/Close split.</remarks>
    /// <param name="ticketId">The ticket to resolve.</param>
    /// <param name="request">
    /// The outcome — one of Resolved, Cancelled, Rejected, Duplicate — the
    /// resolution note, an optional reason code, the duplicate target when
    /// the outcome is Duplicate, and the ticket's current rowVersion.
    /// </param>
    /// <response code="200">The resolved ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/resolution")]
    [RequirePermission(Permissions.TicketsEdit)]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> Resolve(long ticketId, [FromBody] ResolveTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketLifecycleAppService.ResolveAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Close a resolved ticket. CS Agent/CS Supervisor/CS Manager only.</summary>
    /// <remarks>MVP-API-Contracts.md §3.10, ISSUE-022's approved Resolve/Close split.</remarks>
    /// <param name="ticketId">The ticket to close.</param>
    /// <param name="request">The ticket's current rowVersion.</param>
    /// <response code="200">The closed ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/close")]
    [RequirePermission(Permissions.TicketsClose)]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> Close(long ticketId, [FromBody] CloseTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketLifecycleAppService.CloseAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Reopen a closed ticket. CS Agent, CS Supervisor or CS Manager (System Administrator through ADR-0024's override).</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.11 / FR-RES-04, as approved: Reopen is a
    /// domain event, not a status value. A <b>Closed</b> ticket that was
    /// closed as Resolved returns to InProgress in the department the caller
    /// names, its previous owner is cleared, the existing assignment
    /// automation re-runs against that department (leaving it in the
    /// department queue when no rule applies), its ReopenCount increments,
    /// and the prior resolution is archived (never deleted). A Resolved
    /// ticket is NOT reopenable, and neither is one closed as Cancelled,
    /// Rejected or Duplicate.
    ///
    /// <para>
    /// A new <b>Resolution</b> SLA cycle opens at the reopen moment, computed
    /// from the ticket's existing SLA policy; the First Response clock is
    /// carried across untouched and never restarts. The historical cycle is
    /// retained, so the reopened ticket is not breached on the strength of the
    /// original deadline.
    /// </para>
    ///
    /// <para>
    /// Only allowed within ISSUE-011's reopen window (7 days from CLOSURE,
    /// configurable); past it, create a new linked ticket instead.
    /// Authorization is the CS-layer role set — CS Agent, CS Supervisor,
    /// CS Manager (<c>TicketRoleSets.Reopen</c>), plus System Administrator
    /// through ADR-0024 — together with the caller's own access to the
    /// ticket. Every other role receives 403. The action is audited with the actor, the prior and new status,
    /// both departments, both owners and the caller's reason, and appears in
    /// the ticket's lifecycle history.
    /// </para>
    /// </remarks>
    /// <param name="ticketId">The ticket to reopen.</param>
    /// <param name="request">Why the ticket is being reopened, which department takes it on, and the ticket's current rowVersion.</param>
    /// <response code="200">The reopened ticket, back InProgress.</response>
    /// <response code="403">The caller is not CS-layer, or has no access to this ticket.</response>
    /// <response code="400">The request body was malformed, Reason was missing/blank or too long, or TargetDepartmentId was missing.</response>
    /// <response code="404">No such ticket, or the target department does not exist or is inactive.</response>
    /// <response code="409">rowVersion did not match — another request (possibly a concurrent reopen) already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The ticket is not Closed, was closed as Cancelled/Rejected/Duplicate, its request type forbids reopening, or the reopen window has passed.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/reopen")]
    [RequirePermission(Permissions.TicketsReopen)]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> Reopen(long ticketId, [FromBody] ReopenTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        // Shape validation only. The rules themselves live in
        // TicketLifecycleAppService, which enforces them for every caller —
        // these checks exist to answer a malformed request with 400 rather
        // than 422, not to be the enforcement point.
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            ModelState.AddModelError(nameof(request.Reason), "Reason is required.");
            return ValidationProblem();
        }

        if (request.Reason.Trim().Length > TicketLifecycleAppService.ReopenReasonMaxLength)
        {
            ModelState.AddModelError(
                nameof(request.Reason),
                $"Reason must be {TicketLifecycleAppService.ReopenReasonMaxLength} characters or fewer.");
            return ValidationProblem();
        }

        if (request.TargetDepartmentId <= 0)
        {
            ModelState.AddModelError(nameof(request.TargetDepartmentId), "TargetDepartmentId is required.");
            return ValidationProblem();
        }

        var result = await ticketLifecycleAppService.ReopenAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>The ticket's Approvals / Dependencies view: approval cycles (full history), still-requestable configured approvals, typed workflow events, and the derived maintenance/prerequisite states.</summary>
    /// <param name="ticketId">The ticket.</param>
    /// <response code="200">The approvals/dependencies view, with per-caller request/decide capability flags.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [ProducesResponseType<TicketApprovalsViewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{ticketId:long}/approvals")]
    [RequirePermission(Permissions.TicketsView)]
    [Tags(OpenApiTags.Approvals)]
    public async Task<IActionResult> GetApprovals(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketApprovalAppService.GetApprovalsViewAsync(employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => NotFound(),
            _ => NotFound()
        };
    }

    /// <summary>The ticket's lifecycle history — every recorded change to its five lifecycle dimensions, oldest first.</summary>
    /// <remarks>
    /// ADR-0018's append-only <c>TicketStatusHistory</c>, served for the first
    /// time so Ticket Details can show what actually happened to a ticket:
    /// status changes with the note recorded against them (a reopen reason, a
    /// resolution note), verification and escalation transitions, and
    /// system-recorded SLA state changes. Visible to exactly the callers who
    /// can see the ticket itself.
    /// </remarks>
    /// <param name="ticketId">The ticket.</param>
    /// <response code="200">The ticket's lifecycle history.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [ProducesResponseType<TicketLifecycleHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{ticketId:long}/history")]
    [RequirePermission(Permissions.TicketsView)]
    [Tags(OpenApiTags.Tickets)]
    public async Task<IActionResult> GetLifecycleHistory(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketQueryAppService.GetLifecycleHistoryAsync(
            employeeId.Value, GetRoles(), ticketId, cancellationToken);

        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>Open a new approval cycle. Restricted to the ticket's operational actors; the approval type must be configured for the ticket's request type.</summary>
    /// <param name="ticketId">The ticket.</param>
    /// <param name="request">The approval type (e.g. AccountingApproval) and an optional request comment.</param>
    /// <response code="200">The opened approval cycle.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket.</response>
    /// <response code="409">An active cycle of this type already exists.</response>
    /// <response code="422">The approval type is not configured for this ticket's request type, or the ticket is closed.</response>
    [ProducesResponseType<TicketApprovalDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/approvals")]
    [RequirePermission(Permissions.TicketsEdit)]
    // Chairman/CEO is read-only except for the documented right to REQUEST a Reopen Approval
    // (Solution-Analysis.md 4.1); TicketApprovalAppService refuses every other approval type for it.
    [AllowReadOnlyCallerWrite]
    [Tags(OpenApiTags.Approvals)]
    public async Task<IActionResult> RequestApproval(
        long ticketId, [FromBody] RequestApprovalRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketApprovalAppService.RequestApprovalAsync(
            employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Decide a pending approval cycle — Approve, or Reject with a mandatory reason. Restricted to the cycle's configured approval target.</summary>
    /// <param name="ticketId">The ticket.</param>
    /// <param name="approvalId">The approval cycle to decide.</param>
    /// <param name="request">The decision ("Approve"/"Reject") and comment — required for a rejection.</param>
    /// <response code="200">The decided approval cycle.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket/approval.</response>
    /// <response code="409">The cycle is already decided.</response>
    /// <response code="422">A rejection was sent without its reason.</response>
    [ProducesResponseType<TicketApprovalDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/approvals/{approvalId:long}/decision")]
    [RequirePermission(Permissions.TicketsApprove)]
    [Tags(OpenApiTags.Approvals)]
    public async Task<IActionResult> DecideApproval(
        long ticketId, long approvalId, [FromBody] DecideApprovalRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketApprovalAppService.DecideAsync(
            employeeId.Value, GetRoles(), ticketId, approvalId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Withdraw a still-pending approval cycle (kept as history). Restricted to the ticket's operational actors.</summary>
    /// <param name="ticketId">The ticket.</param>
    /// <param name="approvalId">The pending approval cycle to cancel.</param>
    /// <param name="request">An optional cancellation comment.</param>
    /// <response code="200">The cancelled approval cycle.</response>
    /// <response code="404">No such ticket/approval.</response>
    /// <response code="409">The cycle is already decided.</response>
    [ProducesResponseType<TicketApprovalDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [HttpPost("{ticketId:long}/approvals/{approvalId:long}/cancellation")]
    [RequirePermission(Permissions.TicketsEdit)]
    [Tags(OpenApiTags.Approvals)]
    public async Task<IActionResult> CancelApproval(
        long ticketId, long approvalId, [FromBody] CancelApprovalRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketApprovalAppService.CancelAsync(
            employeeId.Value, GetRoles(), ticketId, approvalId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Record a typed dependency event: PrerequisitesCompleted, MaintenanceRequired, MaintenanceNotRequired, or MaintenanceCompleted. Restricted to the ticket's operational actors; approval events are produced only by approval actions.</summary>
    /// <param name="ticketId">The ticket.</param>
    /// <param name="request">The event type and an optional note.</param>
    /// <response code="204">The event was recorded.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket.</response>
    /// <response code="409">The event was already recorded and is not repeatable.</response>
    /// <response code="422">The event does not apply in the ticket's current dependency state, or the ticket is closed.</response>
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/workflow-events")]
    [RequirePermission(Permissions.TicketsEdit)]
    [Tags(OpenApiTags.Approvals)]
    public async Task<IActionResult> RecordWorkflowEvent(
        long ticketId, [FromBody] RecordWorkflowEventRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketApprovalAppService.RecordWorkflowEventAsync(
            employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return result.Outcome == ApprovalMutationOutcome.Success ? NoContent() : ToActionResult(result);
    }

    /// <summary>Link a confirmed CRM match onto a ticket that did not have one at creation. CS Agent/CS Supervisor only.</summary>
    /// <remarks>
    /// See TicketReconciliationAppService's remarks. Scoped to CS Agent/CS
    /// Supervisor — the same actors who own verification-session creation.
    /// </remarks>
    /// <param name="ticketId">The not-yet-verified ticket to reconcile.</param>
    /// <param name="request">The confirmed verification session to consume, and the ticket's current rowVersion.</param>
    /// <response code="200">The reconciled ticket, now verificationStatus Verified.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/reconciliation")]
    [RequirePermission(Permissions.TicketsCreate)]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    [Tags(OpenApiTags.CrmReconciliation)]
    public async Task<IActionResult> Reconcile(long ticketId, [FromBody] ReconcileTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketReconciliationAppService.ReconcileAsync(employeeId.Value, ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Add a note to a ticket.</summary>
    /// <remarks>MVP-API-Contracts.md §4.1.</remarks>
    /// <param name="ticketId">The ticket to annotate.</param>
    /// <param name="request">The note text.</param>
    /// <response code="201">The created note.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpPost("{ticketId:long}/notes")]
    [RequirePermission(Permissions.TicketsEdit)]
    [Tags(OpenApiTags.Notes)]
    [ProducesResponseType<TicketNoteResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddNote(long ticketId, [FromBody] CreateNoteRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketNoteAppService.AddNoteAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return result.Outcome switch
        {
            NoteOutcome.Success => Created($"/api/tickets/{ticketId}/notes", result.Response),
            NoteOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>List a ticket's notes, newest page first.</summary>
    /// <remarks>MVP-API-Contracts.md §4.2.</remarks>
    /// <param name="ticketId">The ticket whose notes to list.</param>
    /// <param name="page">1-based page number. 0 is treated as 1.</param>
    /// <param name="pageSize">Page size. 0 is treated as 50.</param>
    /// <response code="200">A page of notes, with the total count.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}/notes")]
    [RequirePermission(Permissions.TicketsView)]
    [Tags(OpenApiTags.Notes)]
    [ProducesResponseType<TicketNoteListResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListNotes(long ticketId, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketNoteAppService.ListNotesAsync(
            employeeId.Value, GetRoles(), ticketId, page == 0 ? 1 : page, pageSize == 0 ? 50 : pageSize, cancellationToken);
        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    private IActionResult ToActionResult(TicketCreationResult result) => result.Outcome switch
    {
        TicketCreationOutcome.Success =>
            Created($"/api/tickets/{result.Response!.TicketId}", result.Response),

        TicketCreationOutcome.IntakeRecordNotFound => Problem(
            type: "https://tigercs.internal/problems/intake-record-not-found",
            title: "Intake record not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.IntakeRecordAlreadyLinked => Problem(
            type: "https://tigercs.internal/problems/intake-record-already-linked",
            title: "Intake record already promoted",
            detail: "This IntakeRecord is already linked to a ticket.",
            statusCode: StatusCodes.Status409Conflict),

        TicketCreationOutcome.UnitOrContactReferenceMismatch => Problem(
            type: "https://tigercs.internal/problems/unit-or-contact-reference-mismatch",
            title: "UnitReferenceId and ContactReferenceId mismatch",
            detail: "UnitReferenceId and ContactReferenceId must both be supplied or both be omitted.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.UnitReferenceNotFound => Problem(
            type: "https://tigercs.internal/problems/unit-reference-not-found",
            title: "Unit reference not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.ContactReferenceNotFound => Problem(
            type: "https://tigercs.internal/problems/contact-reference-not-found",
            title: "Contact reference not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.CategoryNotFound => Problem(
            type: "https://tigercs.internal/problems/category-not-found",
            title: "Category not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.CategoryDepartmentMismatch => Problem(
            type: "https://tigercs.internal/problems/category-department-mismatch",
            title: "Category does not belong to the Intake department",
            detail: "The selected category does not belong to the Intake department.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.PriorityRequiresCategory => Problem(
            type: "https://tigercs.internal/problems/priority-requires-category",
            title: "Priority cannot be set without a category",
            detail: "An unclassified ticket carries no priority. Priority is judged at classification, together with the category — POST /api/tickets/{ticketId}/classification.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.PriorityNotFound => Problem(
            type: "https://tigercs.internal/problems/priority-not-found",
            title: "Priority not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.DepartmentInactive => Problem(
            type: "https://tigercs.internal/problems/department-inactive",
            title: "Routed department is inactive",
            detail: "This Category routes to a Department that is missing or deactivated.",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.CrmBuyerReferenceMismatch => Problem(
            type: "https://tigercs.internal/problems/crm-buyer-reference-mismatch",
            title: "CRM Buyer identifiers mismatch",
            detail: "CrmBuyerCustomerId, CrmBuyerLeadId, CrmBuyerUnitId, and CrmBuyerProjectId must all be supplied or all be omitted.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.CrmBuyerAndManualProjectUnitBothSupplied => Problem(
            type: "https://tigercs.internal/problems/crm-buyer-and-manual-project-unit-both-supplied",
            title: "CRM Buyer match and manual Project/Unit both supplied",
            detail: "A ticket carries a CRM Buyer match or a manually-entered Project/Unit Number, never both.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.CrmBuyerAndExternalVerificationBothSupplied => Problem(
            type: "https://tigercs.internal/problems/crm-buyer-and-external-verification-both-supplied",
            title: "CRM Buyer match and external verification both supplied",
            detail: "A ticket carries a CRM Buyer match or an external-lookup verification (CustomerVerificationSource), never both.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.ExternalVerificationSourceMissing => Problem(
            type: "https://tigercs.internal/problems/external-verification-source-missing",
            title: "External identifiers supplied without a source",
            detail: "ExternalCustomerId/ExternalUnitId require CustomerVerificationSource.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.TicketNumberCollision => Problem(
            type: "https://tigercs.internal/problems/ticket-number-collision",
            title: "Ticket number collision",
            detail: "A concurrent request generated the same ticket number for this department/day — retry the request.",
            statusCode: StatusCodes.Status409Conflict),

        TicketCreationOutcome.RequestTypeNotFound => Problem(
            type: "https://tigercs.internal/problems/request-type-not-found",
            title: "Request type not found",
            detail: "RequestTypeId did not resolve to an active request type.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.RequestTypeWorkflowNotPublished => Problem(
            type: "https://tigercs.internal/problems/request-type-workflow-not-published",
            title: "Request type workflow is not published",
            detail: "The selected request type's workflow has no published version yet. An administrator must publish it before tickets of this type can be created.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.RequestTypeDepartmentMismatch => Problem(
            type: "https://tigercs.internal/problems/request-type-department-mismatch",
            title: "Request type belongs to another department",
            detail: "The selected request type does not belong to the department this ticket routes to.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.GenesysConversationIdRequired => Problem(
            type: "https://tigercs.internal/problems/genesys-conversation-id-required",
            title: "Genesys conversation id required",
            detail: "A Genesys interaction context requires its ConversationId.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private IActionResult ToActionResult(TicketMutationResult result) => result.Outcome switch
    {
        TicketMutationOutcome.Success => Ok(result.Response),

        TicketMutationOutcome.NotFound => NotFound(),

        TicketMutationOutcome.Forbidden => Forbid(),

        TicketMutationOutcome.ConcurrencyConflict => Problem(
            type: "https://tigercs.internal/problems/ticket-concurrently-modified",
            title: "Ticket concurrently modified",
            detail: "Another request already modified this ticket — reload it and retry.",
            statusCode: StatusCodes.Status409Conflict),

        // Closed-ticket immutability (PR correction): Assign/Transfer/
        // ChangeStatus/Resolve/Close all reject once TicketStatus is
        // Closed — no database changes are made for this outcome.
        TicketMutationOutcome.TicketClosed => Problem(
            type: "https://tigercs.internal/problems/ticket-closed",
            title: "Ticket is closed",
            detail: "This ticket is Closed and no longer accepts assignment, transfer, status changes, resolution, or closing.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.EmployeeNotInDepartment => Problem(
            type: "https://tigercs.internal/problems/employee-not-in-department",
            title: "Employee not in department",
            detail: "The assignee is not an active member of the ticket's resulting department (its current department for Assign; the target department for a transfer with assignToEmployeeId).",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.TransferReasonRequired => Problem(
            type: "https://tigercs.internal/problems/transfer-reason-required",
            title: "A transfer reason is required",
            detail: "This ticket already has an owner. Reassigning it needs a reason: Unable to Resolve, Needs Escalation, Wrong Department, Customer Request or Other.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.TargetDepartmentInactive => Problem(
            type: "https://tigercs.internal/problems/department-inactive",
            title: "Target department is inactive",
            statusCode: StatusCodes.Status404NotFound),

        TicketMutationOutcome.AlreadyInTargetDepartment => Problem(
            type: "https://tigercs.internal/problems/already-in-target-department",
            title: "Ticket is already in the target department",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.InvalidStatusTransition => Problem(
            type: "https://tigercs.internal/problems/invalid-status-transition",
            title: "Invalid status transition",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.TicketNotAssigned => Problem(
            type: "https://tigercs.internal/problems/ticket-not-assigned",
            title: "Ticket has no current owner",
            detail: "Assign the ticket before starting work on it.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.NotEligibleForResolution => Problem(
            type: "https://tigercs.internal/problems/not-eligible-for-resolution",
            title: "Ticket is not eligible for resolution",
            detail: "Resolve is only valid from InProgress or PendingCustomer. Return a legacy Pending Third Party ticket to InProgress first.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.NotYetResolved => Problem(
            type: "https://tigercs.internal/problems/not-yet-resolved",
            title: "Ticket has not been resolved yet",
            statusCode: StatusCodes.Status409Conflict),

        TicketMutationOutcome.NotEligibleForReopen => Problem(
            type: "https://tigercs.internal/problems/not-eligible-for-reopen",
            title: "Ticket is not eligible for reopening",
            detail: "Reopen is only valid on a Closed ticket — a Resolved ticket is still being worked and is corrected in place.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.ResolutionOutcomeNotReopenable => Problem(
            type: "https://tigercs.internal/problems/resolution-outcome-not-reopenable",
            title: "This ticket's outcome cannot be reopened",
            detail: "Only a ticket closed as Resolved, or one cancelled automatically for chatbot inactivity, may be reopened — every other Cancelled, Rejected and Duplicate are final. Raise a new ticket instead.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.ReopenReasonRequired => Problem(
            type: "https://tigercs.internal/problems/reopen-reason-required",
            title: "A reopen reason is required",
            detail: "Every reopen records why it happened.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.TargetDepartmentRequired => Problem(
            type: "https://tigercs.internal/problems/target-department-required",
            title: "A target department is required",
            detail: "Name the department that takes the reopened ticket on.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.ReopenWindowExpired => Problem(
            type: "https://tigercs.internal/problems/reopen-window-expired",
            title: "The reopen window has passed",
            detail: "This ticket's reopen window has expired — create a new ticket for the customer instead.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.DuplicateChainNotAllowed => Problem(
            type: "https://tigercs.internal/problems/duplicate-chain-not-allowed",
            title: "Invalid duplicate target",
            detail: "DuplicateOfTicketId must reference an existing, non-duplicate ticket.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.ReconciliationUnitMismatch => Problem(
            type: "https://tigercs.internal/problems/reconciliation-unit-mismatch",
            title: "Reconciliation unit mismatch",
            detail: "The verification session's unit does not match this ticket's originating raw unit number.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.AlreadyVerified => Problem(
            type: "https://tigercs.internal/problems/ticket-already-verified",
            title: "Ticket is already verified",
            detail: "This ticket already has a linked unit/contact — there is nothing left to reconcile.",
            statusCode: StatusCodes.Status409Conflict),

        TicketMutationOutcome.VerificationSessionNotFound => Problem(
            type: "https://tigercs.internal/problems/verification-session-not-found",
            title: "Verification session not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketMutationOutcome.VerificationSessionForbidden => Forbid(),

        TicketMutationOutcome.VerificationSessionNotConfirmed => Problem(
            type: "https://tigercs.internal/problems/verification-session-not-confirmed",
            title: "Verification session not confirmed",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.VerificationSessionAlreadyConsumed => Problem(
            type: "https://tigercs.internal/problems/verification-session-already-consumed",
            title: "Verification session already consumed",
            statusCode: StatusCodes.Status409Conflict),

        TicketMutationOutcome.VerificationSessionExpired => Problem(
            type: "https://tigercs.internal/problems/verification-session-expired",
            title: "Verification session expired",
            statusCode: StatusCodes.Status410Gone),

        TicketMutationOutcome.PendingReasonRequired => Problem(
            type: "https://tigercs.internal/problems/pending-reason-required",
            title: "Pending reason required",
            detail: "Moving a ticket to PendingCustomer requires a PendingReason.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.NotAllowedForRequestType => Problem(
            type: "https://tigercs.internal/problems/not-allowed-for-request-type",
            title: "Not allowed for this request type",
            detail: "The ticket's request-type workflow configuration does not allow this action.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.AlreadyClassified => Problem(
            type: "https://tigercs.internal/problems/ticket-already-classified",
            title: "Ticket is already classified",
            detail: "This ticket already has a category. Re-categorising an existing ticket is a separate operation and is not available.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.DowngradeRequiresApproval => Problem(
            type: "https://tigercs.internal/problems/downgrade-requires-approval",
            title: "Priority downgrade requires approval",
            detail: "A priority decrease never takes effect directly. Submit POST /api/tickets/{ticketId}/sla/priority-downgrade-requests for Department Head approval.",
            statusCode: StatusCodes.Status403Forbidden),

        TicketMutationOutcome.CategoryNotFound => Problem(
            type: "https://tigercs.internal/problems/category-not-found",
            title: "Category not found",
            detail: "The selected category was not found, or is deactivated.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.CategoryDepartmentMismatch => Problem(
            type: "https://tigercs.internal/problems/category-department-mismatch",
            title: "Category belongs to another department",
            detail: "Classification records what the customer wants; it never re-routes the ticket to another department.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.PriorityNotFound => Problem(
            type: "https://tigercs.internal/problems/priority-not-found",
            title: "Priority not found",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.DisabledByDepartmentSettings => Problem(
            type: "https://tigercs.internal/problems/disabled-by-department-settings",
            title: "Disabled by department workflow settings",
            detail: "The department's workflow settings disable this operation.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private IActionResult ToActionResult(ApprovalMutationResult result) => result.Outcome switch
    {
        ApprovalMutationOutcome.Success => Ok(result.Response),

        ApprovalMutationOutcome.TicketNotFound or ApprovalMutationOutcome.ApprovalNotFound => NotFound(),

        ApprovalMutationOutcome.Forbidden => Forbid(),

        ApprovalMutationOutcome.TicketClosed => Problem(
            type: "https://tigercs.internal/problems/ticket-closed",
            title: "Ticket is closed",
            detail: "This ticket is Closed and no longer accepts approval or dependency actions.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        ApprovalMutationOutcome.ApprovalNotConfigured => Problem(
            type: "https://tigercs.internal/problems/approval-not-configured",
            title: "Approval not configured",
            detail: "This ticket's request type has no active requirement of this approval type.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        ApprovalMutationOutcome.DuplicateActiveApproval => Problem(
            type: "https://tigercs.internal/problems/duplicate-active-approval",
            title: "An active approval of this type already exists",
            statusCode: StatusCodes.Status409Conflict),

        ApprovalMutationOutcome.ApprovalAlreadyDecided => Problem(
            type: "https://tigercs.internal/problems/approval-already-decided",
            title: "Approval already decided",
            detail: "Decisions are write-once — open a new approval cycle instead.",
            statusCode: StatusCodes.Status409Conflict),

        ApprovalMutationOutcome.ReasonRequired => Problem(
            type: "https://tigercs.internal/problems/rejection-reason-required",
            title: "A reason is required",
            detail: "A rejection always carries its why, and a Reopen Approval request carries the reopen reason.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        ApprovalMutationOutcome.ReopenNotEligible => Problem(
            type: "https://tigercs.internal/problems/reopen-not-eligible",
            title: "This ticket is not reopenable",
            detail: "A Reopen Approval is only valid while the ticket could actually be reopened — Closed, closed as Resolved, "
                + "allowed by its request type, and inside the reopen window. Approving one that is no longer executable is refused "
                + "so it can never be granted and then not carried out; the pending request can still be rejected to close it out.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        ApprovalMutationOutcome.InvalidInput => Problem(
            type: "https://tigercs.internal/problems/invalid-approval-input",
            title: "Unsupported approval type, decision, or event type",
            statusCode: StatusCodes.Status400BadRequest),

        ApprovalMutationOutcome.EventAlreadyRecorded => Problem(
            type: "https://tigercs.internal/problems/workflow-event-already-recorded",
            title: "This event was already recorded",
            statusCode: StatusCodes.Status409Conflict),

        ApprovalMutationOutcome.EventNotApplicable => Problem(
            type: "https://tigercs.internal/problems/workflow-event-not-applicable",
            title: "This event does not apply in the ticket's current dependency state",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    /// <summary>The caller's roles, from the JWT's own role claims — never client-supplied, and the app-service layer's sole source of role-based branching (Solution-Analysis.md §4.1's finer-grained per-action role sets, see TicketRoleSets).</summary>
    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}
