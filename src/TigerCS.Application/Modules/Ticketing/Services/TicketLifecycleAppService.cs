using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Core ticket lifecycle: status change (§3.7), resolve (§3.9), close
/// (§3.10) — three deliberately distinct operations, per ISSUE-022's
/// approved Resolve/Department-Employee vs. Close/CS-layer split — and, as
/// of the Customer Workspace phase, reopen (§3.11, FR-RES-04): the Agent's
/// exit from Closed back to InProgress, within ISSUE-011's configurable
/// window (<see cref="ReopenPolicy"/>), re-routing the ticket to a chosen
/// department and opening a new Resolution SLA cycle, while archiving — never
/// deleting — the prior resolution.
/// </summary>
public sealed class TicketLifecycleAppService(
    ITicketRepository ticketRepository,
    ITicketResolutionRepository ticketResolutionRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    SlaBreachProcessor breachProcessor,
    TimeProvider timeProvider,
    ITicketPendingRecordRepository pendingRecordRepository,
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IOutboxWriter outboxWriter,
    IDepartmentRepository departmentRepository,
    IDepartmentWorkflowSettingsRepository departmentWorkflowSettingsRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    TicketAutoAssignmentService autoAssignmentService,
    SlaDueDateService slaDueDateService,
    ReopenEligibilityService reopenEligibilityService,
    SlaPauseService? slaPauseService = null,
    ITicketInteractionHandlerRepository? handlerRepository = null,
    IServiceIdentityRegistry? serviceIdentities = null)
{
    /// <summary>Matches the <c>TicketStatusHistory.Note</c> column, so a long reason is never lost to a database truncation error mid-transaction.</summary>
    public const int ReopenReasonMaxLength = 1000;

    public async Task<TicketMutationResult> ChangeStatusAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ChangeStatusRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (!Enum.TryParse<TicketStatus>(request.NewStatus, ignoreCase: true, out var newStatus))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.InvalidStatusTransition);
        }

        if (!await IsCurrentOwnerOrDepartmentAuthorityAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction): rejected before any
        // transaction/write — see TicketAssignmentAppService.AssignAsync's
        // identical remark.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        // Api enforcement of the retired status, not merely a hidden control:
        // a direct POST naming PendingThirdParty is refused here, before any
        // transaction or write, with the ordinary invalid-transition result.
        // Ticket.ChangeStatus would refuse it too — no arm of the transition
        // table produces it — but rejecting it up front keeps the failure from
        // being reported as "a pending reason is required", which would imply
        // the transition is available once a reason is supplied.
        if (TicketStatusTransitions.IsLegacyOnly(newStatus))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.InvalidStatusTransition);
        }

        // Workflow/Automation phase 2 — structured pending. Entering a
        // Pending status always requires a reason (a ticket is never pending
        // without a recorded why), and, where the ticket carries a request
        // type, it must be allowed by its workflow configuration. The
        // configuration can only narrow the existing status machine — a ticket
        // with no request type keeps the exact pre-phase-2 behavior.
        //
        // PendingCustomer is the only pending kind a ticket can still enter,
        // so CanGoPendingInternal is deliberately no longer consulted: the
        // transition it gated does not exist any more.
        var targetPendingKind = newStatus switch
        {
            TicketStatus.PendingCustomer => PendingKind.Customer,
            _ => (PendingKind?)null
        };

        if (targetPendingKind is not null)
        {
            if (string.IsNullOrWhiteSpace(request.PendingReason))
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.PendingReasonRequired);
            }

            var capabilities = await ResolveCapabilitiesAsync(ticket, cancellationToken);
            if (capabilities?.CanGoPendingCustomer is false)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldStatus = ticket.TicketStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.ChangeStatus(newStatus);
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (InvalidTicketStatusTransitionException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.InvalidStatusTransition);
        }
        catch (TicketNotAssignedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketNotAssigned);
        }

        var correlationId = Guid.NewGuid();

        // The pending record and the resume are written in the same
        // transaction as the status change itself, under the same
        // correlation id, so "went pending"/"resumed" is one auditable event
        // with its structured reason — not a status flip plus a detached
        // note.
        if (targetPendingKind is { } enteringKind)
        {
            await pendingRecordRepository.AddAsync(
                new TicketPendingRecord(
                    ticketId, enteringKind, request.PendingReason!, oldStatus, callerEmployeeId, now, correlationId),
                cancellationToken);
        }
        else if (oldStatus is TicketStatus.PendingCustomer or TicketStatus.PendingThirdParty)
        {
            // PendingThirdParty stays in this test deliberately: it is the
            // legacy escape back to InProgress, and a ticket that went pending
            // before the status was retired must still have its open pending
            // record closed rather than left dangling.
            var openPending = await pendingRecordRepository.GetOpenAsync(ticketId, cancellationToken);
            openPending?.Resume(callerEmployeeId, now);
        }

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)newStatus,
                callerEmployeeId, actorIsSystem: false, note: request.PendingReason, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "ChangeStatus", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(),
            afterValue: targetPendingKind is not null ? $"{newStatus};PendingReason={request.PendingReason}" : newStatus.ToString(),
            correlationId, cancellationToken);

        // ISSUE-018: entering Pending Customer pauses the Resolution SLA
        // (non-Critical), returning to work resumes it and extends the
        // deadline — in this same transaction, so the status and the clock
        // can never disagree.
        await SyncSlaPauseAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }
        catch (DuplicateWriteException)
        {
            // Only reachable when pausing had to record a just-missed
            // deadline first (the breach idempotency key raced a scheduled
            // job). Nothing was half-applied; the caller re-reads and retries.
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    public async Task<TicketMutationResult> ResolveAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ResolveTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (!Enum.TryParse<ResolutionOutcome>(request.ResolutionOutcome, ignoreCase: true, out var outcome))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.InvalidStatusTransition);
        }

        if (!await IsResolveAuthorizedAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction): rejected before any
        // transaction/write.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        if (outcome == ResolutionOutcome.Duplicate)
        {
            var duplicateTarget = request.DuplicateOfTicketId is { } targetId
                ? await ticketRepository.GetByIdAsync(targetId, cancellationToken)
                : null;

            if (duplicateTarget is null || duplicateTarget.ResolutionOutcome == (byte)ResolutionOutcome.Duplicate)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.DuplicateChainNotAllowed);
            }
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldStatus = ticket.TicketStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.Resolve(outcome, request.DuplicateOfTicketId);
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketNotEligibleForResolutionException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForResolution);
        }

        // Resolving directly out of a Pending status ends that pending
        // period — the pause window must close so the record never dangles
        // open on a Resolved ticket. Only PendingCustomer can reach here now
        // (a legacy PendingThirdParty ticket returns to InProgress first, which
        // closes its record on that step), but the test stays as-is rather than
        // narrowing: a record left open on a Resolved ticket is the failure
        // this guards against, and it costs one repository read.
        if (oldStatus is TicketStatus.PendingCustomer or TicketStatus.PendingThirdParty)
        {
            var openPending = await pendingRecordRepository.GetOpenAsync(ticketId, cancellationToken);
            openPending?.Resume(callerEmployeeId, now);
        }

        await ticketResolutionRepository.AddAsync(
            new TicketResolution(
                ticketId, outcome, request.ResolutionNote, request.ReasonCode, request.DuplicateOfTicketId,
                callerEmployeeId, now),
            cancellationToken);

        var correlationId = Guid.NewGuid();

        // A pause still open at this instant ends here (ISSUE-018/SLA §6):
        // closed out first so the deadline is extended by the paused time
        // BEFORE the breach evaluation below judges the resolution against it,
        // and so no pause is left dangling on a Resolved ticket.
        await SyncSlaPauseAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)TicketStatus.Resolved,
                callerEmployeeId, actorIsSystem: false, note: null, correlationId, now),
            cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.ResolutionOutcome, oldValue: null, (byte)outcome,
                callerEmployeeId, actorIsSystem: false, request.ResolutionNote, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Resolve", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(), afterValue: $"ResolutionOutcome={outcome}", correlationId, cancellationToken);

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketResolvedVersion,
            ticket, callerEmployeeId, correlationId, now, cancellationToken);

        // Resolution is the Resolution SLA's achievement event
        // (SLA-Architecture.md §2 — closure deliberately is not), so this is
        // where a late resolution is finalized as a breach. Both clocks are
        // evaluated: a ticket resolved without a First Human Response ever
        // being recorded has missed that target too, and once the ticket
        // reaches Closed nothing may touch its SLA state again, so this is
        // the last honest moment to record it.
        //
        // Runs through the same processor and the same idempotency key as
        // the scheduled job and the sweep, so a deadline a job already
        // flagged is not re-recorded here.
        foreach (var deadlineType in new[] { SlaDeadlineType.FirstResponse, SlaDeadlineType.Resolution })
        {
            await breachProcessor.ProcessDeadlineAsync(ticket, deadlineType, now, correlationId, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }
        catch (DuplicateWriteException)
        {
            // Reachable only since this method began finalizing breach flags:
            // a scheduled deadline job can claim the same breach idempotency
            // key (or the one-auto-escalation-per-ticket index) between this
            // request's read and its commit. The whole transaction rolls
            // back, so the resolution is not half-applied — the caller
            // re-reads and retries, exactly as for a lost RowVersion race.
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    public async Task<TicketMutationResult> CloseAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CloseTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // Whoever the ticket is currently assigned to may close it, whatever their role or department; everyone else needs
        // the CS-layer role. A read-only caller never qualifies through ownership.
        var isCurrentOwner = IsCurrentOwner(callerEmployeeId, callerRoles, ticket);
        if (!AuthorizationGate.Evaluate(callerRoles, () => isCurrentOwner || callerRoles.Any(TicketRoleSets.Close.Contains)))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Close is a cross-department right of the CS layer; a CS Agent may use it only on a ticket they can see.
        if (!isCurrentOwner && !await TicketVisibilityRule.PassesAgentScopeAsync(
                userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction): closing an
        // already-Closed ticket is this condition, not NotYetResolved
        // (checked next) — a Closed ticket always has a current resolution,
        // so without this explicit check first it would otherwise fall
        // through to the resolution lookup below. Rejected before any
        // transaction/write.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        var currentResolution = await ticketResolutionRepository.GetCurrentAsync(ticketId, cancellationToken);
        if (currentResolution is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotYetResolved);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldStatus = ticket.TicketStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.Close();
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketNotYetResolvedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotYetResolved);
        }

        var correlationId = Guid.NewGuid();

        // Normally a no-op (Resolve already closed any pause); defends the
        // invariant that a Closed ticket never carries an open pause.
        await SyncSlaPauseAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)TicketStatus.Closed,
                callerEmployeeId, actorIsSystem: false, note: null, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Close", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(), afterValue: TicketStatus.Closed.ToString(), correlationId, cancellationToken);

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketClosed, OutboxEventTypes.TicketClosedVersion,
            ticket, callerEmployeeId, correlationId, now, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>
    /// Reopen (MVP-API-Contracts.md §3.11, FR-RES-04), implementing the
    /// approved business rule. A <b>Closed</b> ticket — and only a Closed one,
    /// closed as Resolved — returns to InProgress in a department the
    /// reopening CS user names, unowned, with a fresh Resolution SLA cycle.
    ///
    /// <para>
    /// <b>Authorization is two-part.</b> The role gate is the CS layer —
    /// CS Agent, CS Supervisor and CS Manager
    /// (<see cref="TicketRoleSets.Reopen"/>), the final approved rule — and it
    /// is followed by a
    /// resource-level check that the caller could see this ticket at all
    /// (<see cref="TicketVisibilityRule"/>), so enumerating ticket ids reaches
    /// nothing the agent was not already entitled to. Both run through
    /// <see cref="AuthorizationGate"/>, so ADR-0024's System Administrator
    /// override applies as it does to every other operation.
    /// </para>
    ///
    /// <para>
    /// <b>Concurrency is checked before eligibility</b>, deliberately
    /// inverting the order this method used to run in. Two agents reopening at
    /// once both hold the pre-reopen RowVersion; the loser used to find the
    /// ticket already InProgress and be told "not eligible for reopen", which
    /// is true but misleading — nothing is wrong with the ticket, their copy
    /// is stale. Comparing the token first answers that race with the
    /// concurrency conflict it actually is, and the optimistic check at
    /// SaveChanges still covers the narrower window between this read and the
    /// commit. Either way the second request writes nothing: no second cycle,
    /// no second event, no second email.
    /// </para>
    ///
    /// <para>
    /// <b>Everything commits together</b> — the domain transition, the
    /// archived resolution, the new SLA cycle, the re-run of the existing
    /// assignment automation, the lifecycle history, the typed Reopened event
    /// Ticket Details renders, the audit entries and the customer-email Outbox
    /// row — under one correlation id, or nothing does.
    /// </para>
    /// </summary>
    public async Task<TicketMutationResult> ReopenAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ReopenTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // Approved rule: the CS layer reopens (Agent/Supervisor/CS Manager).
        // The role set is consulted through the gate, never inline, so the
        // ADR-0024 System Administrator override stays in one place and every
        // other role — Department Employee/Head, GM, Chairman/CEO, Reporting
        // User — is refused here.
        if (!AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(TicketRoleSets.Reopen.Contains)))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // ...and the resource-level half: an agent may only reopen a ticket
        // they may modify under the existing visibility rules (a creator's read-only access is not enough).
        if (!await TicketVisibilityRule.CanModifyTicketAsync(
                userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Required input, enforced HERE rather than only in model validation:
        // the controller is not the only possible caller, and a reopen with no
        // recorded why is exactly what the approved rule set out to prevent.
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ReopenReasonRequired);
        }

        if (request.TargetDepartmentId <= 0)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TargetDepartmentRequired);
        }

        // See this method's remarks: a stale token is a concurrency conflict,
        // and answering it as one has to happen before the state-based checks
        // that a winning concurrent reopen would otherwise trip.
        if (!ticket.RowVersion.SequenceEqual(request.RowVersion))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        // The approved rule's lifecycle half — Closed, closed as Resolved, the
        // request type still allows Reopen, and the ISSUE-011 window measured
        // from the closure moment in lifecycle history — evaluated by the
        // shared ReopenEligibilityService so this path and the Reopen Approval
        // request/decision path can never disagree about whether a ticket is
        // reopenable. The outcomes below are exactly the ones this method
        // returned when the sequence was inline here.
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The current resolution decides the one carve-out: a Cancelled that
        // is the chatbot inactivity closure. Read before the eligibility
        // check, and used again below to archive it.
        var currentResolution = await ticketResolutionRepository.GetCurrentAsync(ticketId, cancellationToken);
        var closedForInactivity = currentResolution?.ClosedForCustomerInactivity == true;

        switch (await reopenEligibilityService.EvaluateAsync(ticket, now, cancellationToken, closedForInactivity))
        {
            case ReopenEligibility.NotClosed:
            case ReopenEligibility.ClosureMomentUnknown:
                return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
            case ReopenEligibility.OutcomeNotReopenable:
                return TicketMutationResult.Failure(TicketMutationOutcome.ResolutionOutcomeNotReopenable);
            case ReopenEligibility.NotAllowedForRequestType:
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            case ReopenEligibility.WindowExpired:
                return TicketMutationResult.Failure(TicketMutationOutcome.ReopenWindowExpired);
        }

        // A Closed ticket always has a current resolution; a missing one would
        // be data damage — treated as not eligible rather than crashing, since
        // there is no outcome to archive.
        if (currentResolution is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }

        // Routing validation, reusing Transfer's existing department rules
        // rather than inventing a second set (TicketAssignmentAppService.TransferAsync).
        var targetDepartment = await departmentRepository.GetByIdAsync(request.TargetDepartmentId, cancellationToken);
        if (targetDepartment is null || !targetDepartment.IsActive)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TargetDepartmentInactive);
        }

        // ...including the source department's own narrowing setting, but only
        // where the reopen actually moves the ticket out of it. Reopening into
        // the same department is not a transfer, so it is neither blocked by
        // that setting nor rejected as AlreadyInTargetDepartment: naming the
        // closing department is a legitimate reopen, and the owner is cleared
        // and the automation re-run either way.
        if (request.TargetDepartmentId != ticket.CurrentDepartmentId)
        {
            var sourceSettings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(
                ticket.CurrentDepartmentId, cancellationToken);
            if (sourceSettings is { AllowTransferToOtherDepartments: false })
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.DisabledByDepartmentSettings);
            }
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var previousStatus = ticket.TicketStatus;
        var previousDepartmentId = ticket.CurrentDepartmentId;
        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
        var previousSlaState = ticket.SlaState;
        var reason = Truncate(request.Reason.Trim(), ReopenReasonMaxLength);

        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.Reopen(request.TargetDepartmentId, closedForInactivity);
        }
        catch (TicketNotEligibleForReopenException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }
        catch (TicketResolutionOutcomeNotReopenableException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ResolutionOutcomeNotReopenable);
        }

        // A pause can never survive into the new cycle: close anything still
        // open on the period being ended (normally nothing — Resolve/Close
        // already did) before the successor period is opened.
        if (slaPauseService is not null)
        {
            await slaPauseService.CloseOpenPauseAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);
        }

        ticket.RestartSlaClockForReopen();
        currentResolution.Archive();

        // The new Resolution cycle, before the assignment automation so the
        // deadline is available for the activity record below. First Response
        // is carried across untouched — see SlaDueDateService.
        var reopenCycle = await slaDueDateService.StartReopenResolutionCycleAsync(
            ticket, now, callerEmployeeId, correlationId, cancellationToken);

        // The ONE assignment engine, re-evaluated against the department that
        // now owns the work — the same call Transfer makes, for the same
        // reason. Every non-assignable case leaves the ticket in that
        // department's queue, audited.
        var assignment = await autoAssignmentService.ApplyAsync(
            ticket, now, correlationId, AutoAssignmentTrigger.DepartmentTransfer, cancellationToken);

        // Lifecycle history: the status dimension, carrying the agent's reason
        // as its note — the row Ticket Details now reads back through
        // GET /api/tickets/{ticketId}/history.
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)previousStatus, (byte)TicketStatus.InProgress,
                callerEmployeeId, actorIsSystem: false, note: reason, correlationId, now),
            cancellationToken);

        // ...and the SLA dimension, but only when the new cycle actually moved
        // it (Met → Running). A breached clock stays breached, so no row.
        if (ticket.SlaState != previousSlaState)
        {
            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticketId, TicketStatusDimension.SlaState, (byte)previousSlaState, (byte)ticket.SlaState,
                    callerEmployeeId, actorIsSystem: false,
                    note: "New Resolution SLA cycle opened on reopen.", correlationId, now),
                cancellationToken);
        }

        // The typed event, carrying the facts the reason alone cannot express —
        // which department it moved between, where ownership landed, and the
        // new deadline — so the Activity line reads in full without a new
        // activity store. Formatting lives in one place (ReopenActivityFacts).
        var facts = new ReopenActivityFacts(
            previousDepartmentId,
            ticket.CurrentDepartmentId,
            previousOwnerEmployeeId,
            ticket.CurrentOwnerEmployeeId,
            reopenCycle?.ResolutionDueAtUtc,
            ticket.ReopenCount);

        await workflowEventRepository.AddAsync(
            // Where ownership landed is also carried in the typed columns (the type, Reopened, already says why). No
            // transfer reason: a reopen is a lifecycle event, never an escalation and never a counted transfer.
            new TicketWorkflowEvent(
                ticketId, WorkflowEventType.Reopened, now, callerEmployeeId,
                ticketApprovalId: null, facts.Format(), correlationId)
                .WithOwnership(previousDepartmentId, ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Reopen", "Ticket", ticketId.ToString(),
            beforeValue:
                $"{previousStatus};ResolutionOutcome={currentResolution.ResolutionOutcome}"
                + (closedForInactivity ? ";ClosedForCustomerInactivity=True" : string.Empty)
                + $";DepartmentId={previousDepartmentId}"
                + $";AssignedEmployeeId={previousOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}",
            afterValue:
                $"{TicketStatus.InProgress};ReopenCount={ticket.ReopenCount}"
                + $";DepartmentId={ticket.CurrentDepartmentId}"
                + $";AssignedEmployeeId={ticket.CurrentOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}"
                + $";AutoAssignment={assignment.Outcome}"
                + $";ResolutionSlaDueAtUtc={(reopenCycle is { } cycle ? cycle.ResolutionDueAtUtc.ToString("O") : "None")}"
                + $";Reason={reason}",
            correlationId, cancellationToken);

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketReopened, OutboxEventTypes.TicketReopenedVersion,
            ticket, callerEmployeeId, correlationId, now, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>
    /// Brings the SLA pause state in line with the ticket's just-updated
    /// status. A no-op where no <see cref="SlaPauseService"/> is composed
    /// (unit-test compositions that do not exercise SLA pausing).
    /// </summary>
    private Task SyncSlaPauseAsync(
        Ticket ticket, DateTime now, Guid? actorEmployeeId, Guid correlationId, CancellationToken cancellationToken) =>
        slaPauseService is null
            ? Task.CompletedTask
            : slaPauseService.SyncAsync(ticket, now, actorEmployeeId, correlationId, cancellationToken);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// The ticket's effective workflow capabilities, or null when the ticket
    /// carries no request type — null means "no workflow configuration
    /// applies", never "everything forbidden": enforcement in this service
    /// only narrows the existing status machine where configuration exists.
    /// </summary>
    /// <summary>
    /// System closure for chatbot customer inactivity — the existing lifecycle
    /// (Resolved before Closed, status history, resolution row, audit, Outbox
    /// lifecycle event, pending-record resume) executed without a human
    /// caller, because the customer simply stopped answering the chatbot.
    ///
    /// <para>
    /// <b>No role check, and none is needed:</b> this is not reachable from
    /// any controller. Its only caller is
    /// <c>ChatbotInactivityCloseAppService</c>, which has already re-verified
    /// the interaction (still waiting, not ended, no pending human work) and
    /// supplies the integration account that reported the prompt as the
    /// recorded actor. Status history is written with
    /// <c>actorIsSystem: true</c>, so the timeline reads as an automated
    /// action rather than as that account closing the ticket by hand.
    /// </para>
    ///
    /// <para>
    /// <b>Not "resolved".</b> The resolution outcome is
    /// <see cref="ResolutionOutcome.Cancelled"/> and the note carries
    /// <paramref name="reason"/>; the ticket was closed because the customer
    /// went silent, not because the issue was confirmed fixed. Only
    /// <c>TicketClosed</c> is enqueued for the customer — a "your request was
    /// resolved" email would be false. SLA breach finalization is not run:
    /// nothing was worked, and the ticket is Closed in the same step.
    /// </para>
    ///
    /// <para>
    /// <paramref name="beforeCommit"/> runs after the ticket has been
    /// validated and mutated and before the single SaveChanges, so the
    /// caller's own change (clearing the interaction's timer) commits or rolls
    /// back with the closure — and the interaction's concurrency token turns
    /// a reply that raced the close into a rolled-back, no-op result.
    /// </para>
    /// </summary>
    public async Task<TicketMutationResult> CloseForCustomerInactivityAsync(
        Guid integrationEmployeeId,
        long ticketId,
        string reason,
        Action<DateTime>? beforeCommit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        TicketStatus oldStatus;
        try
        {
            oldStatus = ticket.CloseForCustomerInactivity();
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketNotEligibleForResolutionException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForResolution);
        }

        if (oldStatus is TicketStatus.PendingCustomer)
        {
            var openPending = await pendingRecordRepository.GetOpenAsync(ticketId, cancellationToken);
            openPending?.Resume(integrationEmployeeId, now);
        }

        await ticketResolutionRepository.AddAsync(
            TicketResolution.ForCustomerInactivity(ticketId, reason, integrationEmployeeId, now),
            cancellationToken);

        var correlationId = Guid.NewGuid();

        // Straight from Pending Customer to Closed: the open pause ends at
        // this instant, as a system action.
        await SyncSlaPauseAsync(ticket, now, actorEmployeeId: null, correlationId, cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)TicketStatus.Resolved,
                actorEmployeeId: null, actorIsSystem: true, note: reason, correlationId, now),
            cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.ResolutionOutcome, oldValue: null, (byte)ResolutionOutcome.Cancelled,
                actorEmployeeId: null, actorIsSystem: true, note: reason, correlationId, now),
            cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
                actorEmployeeId: null, actorIsSystem: true, note: reason, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            integrationEmployeeId, "AutoCloseCustomerInactivity", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(),
            afterValue: $"{TicketStatus.Closed};ResolutionOutcome={ResolutionOutcome.Cancelled};Reason={reason}",
            correlationId, cancellationToken);

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketClosed, OutboxEventTypes.TicketClosedVersion,
            ticket, integrationEmployeeId, correlationId, now, cancellationToken);

        beforeCommit?.Invoke(now);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>
    /// Closes the ticket behind a Genesys document request once its delivery workflow has ended: Resolved → Closed in one
    /// transaction, as a system action by the integration account. A delivered document closes as <see cref="ResolutionOutcome.Resolved"/>
    /// (customer is told it was resolved and closed); a failed one as <see cref="ResolutionOutcome.Cancelled"/> with the distinct
    /// failure <paramref name="reason"/> (only <c>TicketClosed</c> is sent — "resolved" would be false). The reason and the
    /// delivery <paramref name="detail"/> (document type, code, timestamp) are written to the status history and the audit entry.
    /// A ticket that is already Closed (or not closable) is a no-op: nothing is written twice.
    /// </summary>
    public Task<TicketMutationResult> CloseForDocumentDeliveryAsync(
        Guid integrationEmployeeId,
        long ticketId,
        ResolutionOutcome outcome,
        string reason,
        string detail,
        CancellationToken cancellationToken = default) =>
        CloseBySystemAsync(
            integrationEmployeeId, ticketId, outcome, reason, detail, "AutoCloseDocumentDelivery",
            ticket => ticket.CloseForDocumentDelivery(outcome), beforeCommit: null, cancellationToken);

    /// <summary>
    /// Closes the ticket when the Genesys AI reports it finished the conversation: the same Resolved → Closed system closure as document
    /// delivery, outcome Resolved, with the exact <paramref name="reason"/> the caller supplies ("Closed by AI"). A ticket a person owns, or
    /// one already closed (by anyone, for any reason), is left exactly as it is — nothing is overwritten or written twice.
    /// <paramref name="beforeCommit"/> runs before the single SaveChanges so the caller's own change commits or rolls back with the closure.
    /// </summary>
    public Task<TicketMutationResult> CloseForAiCompletionAsync(
        Guid integrationEmployeeId,
        long ticketId,
        string reason,
        string detail,
        Action<DateTime>? beforeCommit = null,
        CancellationToken cancellationToken = default) =>
        CloseBySystemAsync(
            integrationEmployeeId, ticketId, ResolutionOutcome.Resolved, reason, detail, "AutoCloseByAi",
            ticket => ticket.CloseForAiCompletion(), beforeCommit, cancellationToken);

    private async Task<TicketMutationResult> CloseBySystemAsync(
        Guid integrationEmployeeId,
        long ticketId,
        ResolutionOutcome outcome,
        string reason,
        string detail,
        string auditAction,
        Func<Ticket, TicketStatus> close,
        Action<DateTime>? beforeCommit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        reason = reason.Length <= ReopenReasonMaxLength ? reason : reason[..ReopenReasonMaxLength];
        var note = reason;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        TicketStatus oldStatus;
        try
        {
            oldStatus = close(ticket);
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketNotEligibleForResolutionException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForResolution);
        }

        if (oldStatus is TicketStatus.PendingCustomer)
        {
            var openPending = await pendingRecordRepository.GetOpenAsync(ticketId, cancellationToken);
            openPending?.Resume(integrationEmployeeId, now);
        }

        await ticketResolutionRepository.AddAsync(
            new TicketResolution(ticketId, outcome, reason, reasonCode: null, duplicateOfTicketId: null, integrationEmployeeId, now),
            cancellationToken);

        var correlationId = Guid.NewGuid();

        await SyncSlaPauseAsync(ticket, now, actorEmployeeId: null, correlationId, cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)TicketStatus.Resolved,
                actorEmployeeId: null, actorIsSystem: true, note, correlationId, now),
            cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.ResolutionOutcome, oldValue: null, (byte)outcome,
                actorEmployeeId: null, actorIsSystem: true, note, correlationId, now),
            cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
                actorEmployeeId: null, actorIsSystem: true, note, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            integrationEmployeeId, auditAction, "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(),
            afterValue: $"{TicketStatus.Closed};ResolutionOutcome={outcome};Reason={reason};{detail};AtUtc={now:O}",
            correlationId, cancellationToken);

        if (outcome == ResolutionOutcome.Resolved)
        {
            await EnqueueLifecycleEventAsync(
                OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketResolvedVersion,
                ticket, integrationEmployeeId, correlationId, now, cancellationToken);
            foreach (var deadlineType in new[] { SlaDeadlineType.FirstResponse, SlaDeadlineType.Resolution })
            {
                await breachProcessor.ProcessDeadlineAsync(ticket, deadlineType, now, correlationId, cancellationToken);
            }
        }

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketClosed, OutboxEventTypes.TicketClosedVersion,
            ticket, integrationEmployeeId, correlationId, now, cancellationToken);

        beforeCommit?.Invoke(now);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>
    /// Records a customer-facing lifecycle event in the transactional Outbox
    /// (ADR-0013) — the same mechanism <c>TicketCreationAppService</c> uses
    /// for <c>TicketCreated</c>. Added to the caller's unit of work, so the
    /// event commits with the state change or not at all: a rolled-back
    /// resolve/close/reopen leaves no event behind, and nothing is sent
    /// from inside the request (NFR-REL-01). The customer email itself is
    /// rendered and delivered later by the Outbox dispatcher, so an SMTP
    /// failure can never fail this operation.
    /// </summary>
    private async Task EnqueueLifecycleEventAsync(
        string eventType,
        int eventVersion,
        Ticket ticket,
        Guid callerEmployeeId,
        Guid correlationId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var payload = new TicketLifecycleEventPayload(ticket.TicketId, eventVersion, ticket.ReopenCount);

        var message = await outboxWriter.WriteAsync(
            eventType,
            payload.ToJson(),
            correlationId,
            OutboxEventTypes.LifecycleIdempotencyKeyFor(eventType, ticket.TicketId, eventVersion, ticket.ReopenCount),
            nowUtc,
            cancellationToken);

        if (message is null)
        {
            // Already enqueued for this occurrence (a retried request); the
            // first row stands.
            return;
        }

        await auditWriter.WriteAsync(
            callerEmployeeId,
            NotificationAuditActions.NotificationQueued,
            NotificationAuditActions.OutboxMessageEntityType,
            message.OutboxMessageId.ToString(),
            beforeValue: null,
            afterValue: $"EventType={eventType};TicketId={ticket.TicketId};Status=Pending",
            correlationId,
            cancellationToken);
    }

    private async Task<WorkflowCapabilities?> ResolveCapabilitiesAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        if (ticket.RequestTypeId is not { } requestTypeId)
        {
            return null;
        }

        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return null;
        }

        // The ticket's PINNED version is authoritative (Administration /
        // Workflow Designer phase): publishing a newer version never changes
        // what an existing ticket may do. A legacy ticket that carries a
        // request type but no pinned version (classified before versioning,
        // and not backfilled) falls back to the workflow's currently
        // Published version — the same resolution it always had.
        var template = ticket.WorkflowTemplateId is { } pinnedVersionId
            ? await workflowTemplateRepository.GetByIdAsync(pinnedVersionId, cancellationToken)
            : await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
        return template is null ? null : WorkflowCapabilities.Resolve(template, requestType);
    }

    private Task<bool> IsCurrentOwnerOrDepartmentAuthorityAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            if (Roles.IsReadOnlyCaller(callerRoles))
            {
                return false;
            }

            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId)
            {
                return true;
            }

            if (callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains))
            {
                return true;
            }

            return callerRoles.Contains(Roles.DepartmentHead)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
        });

    /// <summary>ISSUE-022: Resolve is Department Employee/Head only. A Department Employee must be the ticket's current owner (the one who actually worked it); a Department Head may resolve any ticket in a department they belong to. Both are permission rules, so both run under the ADR-0024 override — the ticket's own eligibility for resolution (<see cref="Ticket.Resolve"/>) is separate, and is not.</summary>
    private Task<bool> IsResolveAuthorizedAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            // The current assignee may always resolve, regardless of role or department.
            if (IsCurrentOwner(callerEmployeeId, callerRoles, ticket))
            {
                return true;
            }

            if (!callerRoles.Any(TicketRoleSets.Resolve.Contains))
            {
                return false;
            }

            if (callerRoles.Contains(Roles.DepartmentHead))
            {
                return await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
            }

            return ticket.CurrentOwnerEmployeeId == callerEmployeeId;
        });

    /// <summary>The user the ticket is currently assigned to — the rule behind owner-based Resolve/Close. Read-only roles never qualify.</summary>
    private static bool IsCurrentOwner(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket) =>
        !Roles.IsReadOnlyCaller(callerRoles) && ticket.CurrentOwnerEmployeeId == callerEmployeeId;
}
