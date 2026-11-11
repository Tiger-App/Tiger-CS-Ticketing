using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The agent-facing half of pending human work: seeing what is waiting, and
/// taking it, finishing it, or standing it down.
///
/// <para>
/// <b>Deliberately not a routing engine.</b> Nothing here decides who should
/// get the work, dials anyone, sends a WhatsApp message or resumes a chat.
/// Genesys owns channel delivery, queue routing, agent availability, live
/// transfer and outbound execution. This service owns the business-visible
/// state of the work: who is waiting, since when, on which channel, for which
/// ticket, and whether a human has picked it up.
/// </para>
///
/// <para>
/// <b>Nothing here touches the ticket.</b> Completing human work is not
/// resolving a case: the customer whose chat was answered may still have an
/// NOC workflow running for days. The ticket keeps following the existing
/// TigerCS lifecycle, and the two statuses are read side by side.
/// </para>
///
/// <para>
/// <b>Every mutation is idempotent.</b> Starting work already in progress,
/// or completing work already completed, is answered rather than duplicated —
/// a double-clicked button and a redelivered event behave the same way.
/// </para>
/// </summary>
public sealed class AgentHandoffAppService(
    ITicketAgentHandoffRepository handoffRepository,
    TicketQueryAppService ticketQueryAppService,
    ITicketRepository ticketRepository,
    ITicketAssignmentRepository ticketAssignmentRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    ITicketNoteRepository? ticketNoteRepository = null,
    IOutboxWriter? outboxWriter = null)
{
    private readonly TicketNotificationEvents? _notificationEvents = TicketNotificationEvents.CreateOrNull(outboxWriter, auditWriter);

    /// <summary>
    /// The agent work list — every customer interaction waiting for a human,
    /// on every channel. Scoped by exactly the same visible-department rule
    /// as the ticket queue, so an agent never sees work they could not open.
    /// </summary>
    public async Task<AgentHandoffListResultDto> ListAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        AgentHandoffListRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);
        var agentScope = ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles);
        var claimableDepartmentIds = agentScope is null ? null : await ClaimableDepartmentIdsAsync(callerEmployeeId, cancellationToken);

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var result = await handoffRepository.SearchAsync(
            new AgentHandoffQuery(
                visibleDepartmentIds, request.DepartmentId, request.ChannelId, request.AssignedEmployeeId,
                request.UnassignedOnly, request.IncludeResolved, page, pageSize, agentScope, claimableDepartmentIds),
            cancellationToken);

        return new AgentHandoffListResultDto(
            result.Items.Select(ToDto).ToList(), result.TotalCount, page, pageSize);
    }

    /// <summary>A ticket's handoff history, for Ticket Details. Newest request first.</summary>
    public Task<IReadOnlyList<TicketAgentHandoff>> ListByTicketAsync(long ticketId, CancellationToken cancellationToken = default) =>
        handoffRepository.ListByTicketIdAsync(ticketId, cancellationToken);

    /// <summary>
    /// A human agent <b>accepts</b> pending work: claims it exclusively, takes
    /// ownership of the ticket behind it, and starts the work — all in one
    /// transaction, or none of it.
    ///
    /// <para>
    /// <b>Exclusive.</b> <see cref="TicketAgentHandoff.ClaimBy"/> refuses a
    /// second agent with the holder's identity, so the loser is told who has
    /// it instead of receiving a success carrying someone else's name. The
    /// same agent repeating the call is idempotent. The handoff's
    /// <c>RowVersion</c> closes the narrower window where two requests both
    /// read an unclaimed row and interleave their commits.
    /// </para>
    ///
    /// <para>
    /// <b>Department membership is a PRECONDITION, not a conditional.</b> An
    /// employee may accept only if they are an active member of the ticket's
    /// <c>CurrentDepartmentId</c>. Checked before a single thing is written, so
    /// a caller who cannot own the ticket changes nothing at all: the handoff
    /// stays <see cref="AgentHandoffStatus.WaitingForAgent"/>, its
    /// <c>AssignedEmployeeId</c> stays null, and the ticket stays Open and
    /// unowned. They receive
    /// <see cref="AgentHandoffOutcome.AgentNotInTicketDepartment"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Why a precondition rather than "assign where possible".</b> Accepting
    /// is one indivisible business act — take the work, own the ticket, start
    /// it. Claiming the work while declining to assign the ticket produces the
    /// state this method exists to avoid: handoff InProgress, which tells the
    /// queue somebody is on it, while the ticket sits Open and unowned, which
    /// tells the ticket nobody is. The invariant being protected is
    /// MVP-API-Contracts.md §3.5's — an assignee must belong to the ticket's
    /// current department, which
    /// <c>TicketAssignmentAppService.AssignAsync</c> already refuses as
    /// <c>EmployeeNotInDepartment</c> — and it is not relaxed here.
    /// </para>
    ///
    /// <para>
    /// <b>The cross-department route is Department Transfer.</b> A human from
    /// another department who needs this work transfers the ticket first
    /// (<c>POST /api/tickets/{id}/transfer</c>); a member of the new current
    /// department then accepts. The handoff's own department follows the ticket,
    /// so the work stays on the same list it always was.
    /// </para>
    ///
    /// <para>
    /// <b>ADR-0024's override does not reach the membership rule.</b> It still
    /// carries authorization and visibility — a System Administrator sees and
    /// may act on this work in any department — but membership of the
    /// <i>assignee</i> is a domain invariant about the ticket's data, not a
    /// permission, so the administrator is refused here exactly as anyone else
    /// is. That is why the check below calls the repository directly rather
    /// than through <c>AuthorizationGate</c>.
    /// </para>
    ///
    /// <para>
    /// <b>First Response is deliberately NOT recorded here</b> (confirmed
    /// decision 10). Accepting is not replying: the customer has heard nothing
    /// yet. <c>Ticket.FirstHumanResponseAtUtc</c> is satisfied by the first
    /// actual human message — see
    /// <c>GenesysConversationEndAppService</c>'s transcript path. Acceptance
    /// time is the end of the separate Human Wait operational metric
    /// (<c>RequestedAtUtc</c> → <c>AssignedAtUtc</c>), which is not an SLA.
    /// </para>
    /// </summary>
    public async Task<AgentHandoffResult> AcceptAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketAgentHandoffId,
        CancellationToken cancellationToken = default)
    {
        var handoff = await handoffRepository.GetByIdAsync(ticketAgentHandoffId, cancellationToken);
        if (handoff is null)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.NotFound);
        }

        // Same authority as every other action on this work list: ordinary
        // work in a department the caller can already see, carrying ADR-0024's
        // override through AuthorizationGate.
        // Read-only roles (Chairman/CEO, Reporting User) view the work list but never act on it.
        if (Roles.IsReadOnlyCaller(callerRoles)
            || !await CanViewHandoffAsync(callerEmployeeId, callerRoles, handoff, cancellationToken, allowClaimException: true))
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.Forbidden);
        }

        var ticket = await ticketRepository.GetByIdAsync(handoff.TicketId, cancellationToken);
        if (ticket is null)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.NotFound);
        }

        // ---- Preconditions. Every one of these is evaluated BEFORE the
        // transaction opens, so a refusal leaves the handoff and the ticket
        // exactly as they were. ----

        // Closed-ticket immutability: a Closed ticket can take neither an owner
        // nor a status change, so accepting could never complete. The work is
        // stood down instead, not claimed.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.TicketClosed);
        }

        // The approved rule, and the reason this method cannot produce a
        // half-accepted state. Read straight from the repository — NOT through
        // AuthorizationGate — because this is the assignee's membership of the
        // ticket's department, a domain invariant, rather than a question about
        // what the caller is permitted to do.
        // The one exception is the CS Agent who ALREADY owns the ticket: a department change keeps the responsible CS Agent on it, so they
        // are not a member of the receiving department and must not have to take ownership again. Nothing is assigned to them here
        // (they already own it), so the invariant this guards - never assigning a non-member - is not at risk.
        if (ticket.CurrentOwnerEmployeeId != callerEmployeeId
            && !await userDepartmentAssignmentRepository.ExistsAsync(
                callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.AgentNotInTicketDepartment);
        }

        // One owner at a time (checked before anything is claimed or written, and only while the work is unclaimed or already this caller's, so a second agent still hears AlreadyClaimed): a ticket a different person genuinely owns is not taken by accepting. Automation's pre-assignment (or no
        // owner) is the only thing accepting replaces; anything else is a deliberate Assign/Transfer by someone with that authority.
        if ((handoff.AssignedEmployeeId is null || handoff.AssignedEmployeeId == callerEmployeeId)
            && ticket.CurrentOwnerEmployeeId is { } existingOwner
            && existingOwner != callerEmployeeId
            && !await TicketOwnershipRules.IsAutomaticPreAssignmentAsync(ticketAssignmentRepository, ticket, cancellationToken, ticketNoteRepository))
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.TicketOwnedByAnotherEmployee);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();
        var beforeStatus = handoff.Status;
        var beforeTicketStatus = ticket.TicketStatus;
        var beforeOwner = ticket.CurrentOwnerEmployeeId;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        bool claimed;
        try
        {
            claimed = handoff.ClaimBy(callerEmployeeId, now);
        }
        catch (AgentHandoffAlreadyResolvedException)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.AlreadyResolved);
        }
        catch (AgentHandoffAlreadyClaimedException alreadyClaimed)
        {
            return AgentHandoffResult.AlreadyClaimed(alreadyClaimed.HolderEmployeeId, alreadyClaimed.ClaimedAtUtc);
        }

        // The accepting agent owns the ticket. Unconditional by the approved
        // rule, and safe precisely because membership was established above:
        // this cannot assign a non-member. Skipped only when they already own
        // it, so a repeated accept does not append a duplicate assignment row.
        var assignedTicket = false;
        if (ticket.CurrentOwnerEmployeeId != callerEmployeeId)
        {
            var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
            currentAssignment?.MarkSuperseded();

            ticket.AssignTo(callerEmployeeId);
            // The accepting agent is the first eligible Human Agent of a ticket the AI / integration created. Write-once, so a later accept by
            // someone else never changes it.
            ticket.RecordOriginalHumanAgent(callerEmployeeId);
            await ticketAssignmentRepository.AddAsync(
                new TicketAssignment(ticket.TicketId, callerEmployeeId, ticket.CurrentDepartmentId, now, callerEmployeeId),
                cancellationToken);
            assignedTicket = true;
        }

        // ...and the work starts. The ticket now has an owner, so the domain's
        // owner guard on Open → InProgress is satisfied by construction — which
        // is the whole reason assignment comes first.
        var movedToInProgress = false;
        if (ticket.TicketStatus == TicketStatus.Open)
        {
            ticket.ChangeStatus(TicketStatus.InProgress);
            movedToInProgress = true;

            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticket.TicketId, TicketStatusDimension.TicketStatus,
                    (byte)TicketStatus.Open, (byte)TicketStatus.InProgress,
                    callerEmployeeId, actorIsSystem: false,
                    note: "A human agent accepted the pending customer interaction.",
                    correlationId, now),
                cancellationToken);
        }

        if (assignedTicket && _notificationEvents is not null)
        {
            // Queued after the status move so the customer email describes the ticket as it now is (a person is working on it). One
            // event for "accepted and started": a separate "in progress" email seconds later would say the same thing twice.
            await _notificationEvents.QueueAssignedAsync(ticket, beforeOwner, callerEmployeeId, callerEmployeeId, correlationId, now, cancellationToken);
        }

        // Ticket Activity: only when this call actually performed the claim,
        // so a repeated accept by the holder does not append a second line.
        if (claimed)
        {
            var started = new TicketWorkflowEvent(
                handoff.TicketId, WorkflowEventType.HandoffStarted, now, callerEmployeeId,
                ticketApprovalId: null,
                note: FormatWaitNote(handoff),
                correlationId);

            // The accepting agent became the owner: carry where ownership landed in the typed columns of this same event.
            // HandoffStarted already says why; no transfer reason, so it is never an escalation or a counted transfer.
            if (assignedTicket)
            {
                started.WithOwnership(ticket.CurrentDepartmentId, ticket.CurrentDepartmentId, callerEmployeeId);
            }

            await workflowEventRepository.AddAsync(started, cancellationToken);
        }
        else if (assignedTicket)
        {
            // The rare repeat: the same agent who already holds the work item accepts again AFTER the ticket was moved to someone
            // else, and the approved "accepting agent owns the ticket" rule takes it back. No second claim happened, but ownership
            // really changed, so it is recorded - once, with where it landed. A plain repeat (the agent still owns it) changes
            // nothing and writes nothing, so every actual ownership change has exactly one event.
            await workflowEventRepository.AddAsync(
                new TicketWorkflowEvent(
                    handoff.TicketId, WorkflowEventType.HandoffStarted, now, callerEmployeeId,
                    ticketApprovalId: null,
                    note: "Accepted again: the accepting agent took the ticket back.",
                    correlationId)
                    .WithOwnership(ticket.CurrentDepartmentId, ticket.CurrentDepartmentId, callerEmployeeId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerEmployeeId, "AcceptAgentHandoff", nameof(TicketAgentHandoff),
            handoff.TicketAgentHandoffId.ToString(),
            beforeValue:
                $"Status={beforeStatus};TicketStatus={beforeTicketStatus};"
                + $"AssignedEmployeeId={beforeOwner?.ToString() ?? "DepartmentQueue"}",
            afterValue:
                $"Status={handoff.Status};TicketStatus={ticket.TicketStatus};"
                + $"AssignedEmployeeId={ticket.CurrentOwnerEmployeeId?.ToString() ?? "DepartmentQueue"};"
                + $"ClaimPerformed={claimed};TicketAssigned={assignedTicket};MovedToInProgress={movedToInProgress};"
                + $"HumanWaitSeconds={(handoff.AssignedAtUtc is { } at ? (at - handoff.RequestedAtUtc).TotalSeconds : 0):F0}",
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            // Either the ticket's RowVersion or the handoff's moved (or a concurrent request already became the current assignee) between
            // this request's read and its commit. Nothing was written.
            return AgentHandoffResult.Failure(AgentHandoffOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return AgentHandoffResult.Success(ToDto(handoff));
    }

    /// <summary>
    /// The human work is done. <b>The ticket is untouched</b> — it stays open,
    /// in progress, or wherever the existing workflow has it.
    /// </summary>
    public Task<AgentHandoffResult> CompleteAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketAgentHandoffId,
        CompleteAgentHandoffRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MutateAsync(
            callerEmployeeId, callerRoles, ticketAgentHandoffId, "CompleteAgentHandoff",
            (handoff, now) => handoff.Complete(callerEmployeeId, now, request.ResolutionNote),
            cancellationToken);
    }

    /// <summary>The work is no longer needed. A reason is required — pending customer work is never dropped silently.</summary>
    public Task<AgentHandoffResult> CancelAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketAgentHandoffId,
        CancelAgentHandoffRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Task.FromResult(AgentHandoffResult.Failure(AgentHandoffOutcome.ReasonRequired));
        }

        return MutateAsync(
            callerEmployeeId, callerRoles, ticketAgentHandoffId, "CancelAgentHandoff",
            (handoff, now) => handoff.Cancel(now, request.Reason),
            cancellationToken);
    }

    /// <summary>How long the customer waited for a person — the Human Wait metric, in the Activity line that ends it.</summary>
    private static string FormatWaitNote(TicketAgentHandoff handoff)
    {
        var waitedFor = (handoff.AssignedAtUtc ?? handoff.RequestedAtUtc) - handoff.RequestedAtUtc;
        return $"Accepted after {FormatDuration(waitedFor)} waiting for a human agent.";
    }

    /// <summary>Human-readable duration for an Activity line — never a raw TimeSpan.</summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalMinutes < 1
            ? $"{duration.TotalSeconds:F0}s"
            : duration.TotalHours < 1
                ? $"{duration.TotalMinutes:F0}m"
                : duration.TotalDays < 1
                    ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                    : $"{(int)duration.TotalDays}d {duration.Hours}h";
    }

    private async Task<AgentHandoffResult> MutateAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketAgentHandoffId,
        string auditAction,
        Action<TicketAgentHandoff, DateTime> mutate,
        CancellationToken cancellationToken)
    {
        var handoff = await handoffRepository.GetByIdAsync(ticketAgentHandoffId, cancellationToken);
        if (handoff is null)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.NotFound);
        }

        // Acting on pending work is ordinary work in a department you can
        // already see — the same authority the ticket queue grants, never a
        // new privilege tier.
        // Read-only roles (Chairman/CEO, Reporting User) view the work list but never act on it.
        if (Roles.IsReadOnlyCaller(callerRoles)
            || !await CanViewHandoffAsync(callerEmployeeId, callerRoles, handoff, cancellationToken))
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.Forbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();
        var beforeStatus = handoff.Status;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            mutate(handoff, now);
        }
        catch (AgentHandoffAlreadyResolvedException)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.AlreadyResolved);
        }

        // Ticket Activity, for the transitions that have a typed event. Start
        // is deliberately absent: the accept path (AcceptAsync) is what a
        // human pressing the button goes through, and it emits HandoffStarted
        // itself — emitting here too would double-count.
        var activityEvent = handoff.Status switch
        {
            AgentHandoffStatus.Completed => WorkflowEventType.HandoffCompleted,
            AgentHandoffStatus.Cancelled => WorkflowEventType.HandoffCancelled,
            _ => (WorkflowEventType?)null
        };

        if (activityEvent is { } eventType && beforeStatus != handoff.Status)
        {
            await workflowEventRepository.AddAsync(
                new TicketWorkflowEvent(
                    handoff.TicketId, eventType, now, callerEmployeeId,
                    ticketApprovalId: null, handoff.ResolutionNote, correlationId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerEmployeeId, auditAction, nameof(TicketAgentHandoff), handoff.TicketAgentHandoffId.ToString(),
            beforeValue: $"Status={beforeStatus}",
            afterValue: $"Status={handoff.Status};AssignedEmployeeId={handoff.AssignedEmployeeId?.ToString() ?? "(none)"}",
            correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return AgentHandoffResult.Success(ToDto(handoff));
    }

    /// <summary>The list projection, with the ticket/interaction context the row carries.</summary>
    internal static AgentHandoffDto ToDto(AgentHandoffListRow row) =>
        ToDto(row.Handoff, row.TicketNumber, row.RequestSummary, row.TicketStatus, row.TicketIsClassified,
            row.CustomerName, row.CustomerPhone, row.GenesysConversationId, row.InteractionEnded);

    /// <summary>The single-handoff projection. The ticket/interaction context is not re-read for a mutation response — the action changed the work item, not the ticket.</summary>
    internal static AgentHandoffDto ToDto(TicketAgentHandoff handoff) =>
        ToDto(handoff, ticketNumber: string.Empty, requestSummary: string.Empty, ticketStatus: string.Empty,
            ticketIsClassified: false, customerName: null, customerPhone: null,
            genesysConversationId: null, interactionEnded: false);

    private static AgentHandoffDto ToDto(
        TicketAgentHandoff handoff, string ticketNumber, string requestSummary, string ticketStatus,
        bool ticketIsClassified, string? customerName, string? customerPhone,
        string? genesysConversationId, bool interactionEnded) =>
        new(handoff.TicketAgentHandoffId,
            handoff.TicketId,
            ticketNumber,
            handoff.TicketInteractionId,
            genesysConversationId,
            handoff.DepartmentId,
            handoff.ChannelId,
            handoff.Status.ToString(),
            handoff.Mode?.ToString(),
            handoff.RequestReason,
            handoff.RequestedAtUtc,
            handoff.AssignedEmployeeId,
            handoff.GenesysAgentId,
            handoff.AssignedAtUtc,
            handoff.StartedAtUtc,
            handoff.CompletedAtUtc,
            handoff.ResolvedAtUtc,
            handoff.ResolutionNote,
            customerName,
            customerPhone,
            requestSummary,
            ticketStatus,
            ticketIsClassified,
            interactionEnded,
            handoff.ExternalWorkItemId);

    /// <summary>
    /// May the caller see (and so act on) this work item. Acting needs Full access to its ticket, never just creator read-only access. By its department for everyone except a restricted CS Agent, who sees it only
    /// when they can see its ticket — assigned to them, or handled by them on Genesys.
    ///
    /// <para>
    /// <paramref name="allowClaimException"/> (set only by <see cref="AcceptAsync"/>) adds the claim route: a restricted agent may also
    /// try to claim <b>unresolved</b> work in a department they belong to, which is how they take it. If a colleague got there first they
    /// are told who holds it (the exclusive claim) rather than refused blind. It never lets them complete or cancel work, and never opens the
    /// ticket itself. The exclusive claim, the ticket-department membership precondition and the RowVersion check in
    /// <see cref="AcceptAsync"/> still apply.
    /// </para>
    /// </summary>
    private async Task<bool> CanViewHandoffAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, TicketAgentHandoff handoff, CancellationToken cancellationToken,
        bool allowClaimException = false)
    {
        // A regular Department Employee is assignment-only too: they may act on the work item of a ticket assigned to them, and have no
        // department-level view of work items (nor the agent claim route below).
        var assignmentOnlyEmployee = TicketVisibilityRule.IsAssignmentOnlyEmployee(callerRoles);
        if (!assignmentOnlyEmployee && !ticketQueryAppService.IsRestrictedAgent(callerEmployeeId, callerRoles))
        {
            return await ticketQueryAppService.CanViewDepartmentAsync(callerEmployeeId, callerRoles, handoff.DepartmentId, cancellationToken);
        }

        // Acting on a work item (claim, complete, cancel) is modifying its ticket: Full access only. A creator's read-only access to a ticket
        // in another department never lets them touch its work.
        if (await ticketQueryAppService.CanModifyTicketAsync(callerEmployeeId, callerRoles, handoff.TicketId, cancellationToken))
        {
            return true;
        }

        if (!allowClaimException || assignmentOnlyEmployee || handoff.ResolvedAtUtc is not null)
        {
            return false;
        }

        // A member of the work item's department, or of the ticket's CURRENT department (the approved route: transfer first, then a member of
        // the new current department accepts). Membership here is only the claim route; AcceptAsync still requires membership of the ticket's
        // current department before anything is assigned.
        if (await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, handoff.DepartmentId, cancellationToken))
        {
            return true;
        }

        var ticket = await ticketRepository.GetByIdAsync(handoff.TicketId, cancellationToken);
        return ticket is not null
            && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
    }

    /// <summary>The departments the caller belongs to — where a restricted CS Agent may claim waiting work.</summary>
    private async Task<IReadOnlyCollection<int>> ClaimableDepartmentIdsAsync(Guid callerEmployeeId, CancellationToken cancellationToken) =>
        [.. (await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken)).Select(a => a.DepartmentId)];
}
